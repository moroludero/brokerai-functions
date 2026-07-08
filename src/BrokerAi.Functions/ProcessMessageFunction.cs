using BrokerAi.Core.Data;
using BrokerAi.Core.Data.Entities;
using BrokerAi.Core.Domain;
using BrokerAi.Core.Options;
using BrokerAi.Core.Services;
using BrokerAi.Core.Webhook;
using Microsoft.EntityFrameworkCore;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text;
using System.Text.Json;

namespace BrokerAi.Functions;

/// <summary>
/// Workflow 1 body (queue-triggered). Voice → canned reply. Broker lookup by
/// phone_number_id or alert_number. Sender is the broker → BrokerCommandRouter
/// / PropertyIntakeStateMachine (Workflow 2). Otherwise → lead qualification
/// (Workflow 3): QR check → classification → off-topic guard → plan gate →
/// step advance → scoring/hot-alert.
/// </summary>
public sealed class ProcessMessageFunction(
    BrokerAiDbContext db,
    MessageRouter router,
    IClaudeGateway claude,
    IWhatsAppSender sender,
    IMediaService media,
    IFacebookService facebook,
    IOptions<AppOptions> appOptions,
    ILogger<ProcessMessageFunction> logger)
{
    [Function("ProcessMessage")]
    public async Task Run(
        [QueueTrigger("incoming-messages")] string queueItem,
        CancellationToken ct)
    {
        // The queue extension base64-decodes the message before invoking us, so
        // queueItem is already the JSON produced by the webhook. Only attempt a
        // base64 decode if it clearly isn't JSON (defensive for manual requeues).
        var payload = queueItem.TrimStart().StartsWith('{')
            ? queueItem
            : System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(queueItem));
        var msg = JsonSerializer.Deserialize<IncomingMessage>(payload)
            ?? throw new InvalidOperationException("Empty queue message");

        // Idempotency: Meta redelivers, and queue retries can re-run.
        if (await db.ProcessedMessages.AnyAsync(p => p.MessageId == msg.MessageId, ct))
        {
            logger.LogInformation("Skipping already-processed message {MessageId}", msg.MessageId);
            return;
        }

        var (broker, isBroker) = await router.ResolveAsync(msg.PhoneNumberId, msg.From, ct);
        if (broker is null)
        {
            logger.LogWarning("Unknown sender {From} on phone_number_id {Pni} — dropping", msg.From, msg.PhoneNumberId);
            await MarkProcessedAsync(msg.MessageId, ct);
            return;
        }

        if (msg.IsVoice)
        {
            await sender.SendTextAsync(msg.PhoneNumberId, msg.From, WebhookParser.VoiceAutoReply, ct);
            await MarkProcessedAsync(msg.MessageId, ct);
            return;
        }

        if (isBroker)
            await HandleBrokerMessageAsync(broker, msg, ct);
        else
            await HandleLeadMessageAsync(broker, msg, ct);

        await MarkProcessedAsync(msg.MessageId, ct);
    }

    // ---------------------------------------------------------------- Lead flow (Workflow 3)

    private async Task HandleLeadMessageAsync(Broker broker, IncomingMessage msg, CancellationToken ct)
    {
        var session = await GetOrCreateSessionAsync(broker.Id, msg.From, SessionType.Lead, ct);
        var lead = await GetOrCreateLeadAsync(broker.Id, msg.From, ct);
        var isFirstMessage = session.Step == LeadSteps.Greeting && lead.CreatedAt == lead.UpdatedAt;

        // WhatsApp sends the sender's profile display name with every message —
        // free name capture, so the hot-lead alert can identify the person.
        if (string.IsNullOrWhiteSpace(lead.Name) && !string.IsNullOrWhiteSpace(msg.ProfileName))
            lead.Name = msg.ProfileName;

        session.Context.History.Add(new TurnRecord { Role = "user", Content = msg.Text ?? "" });

        // QR scan short-circuits before any AI call.
        string? qrShortCode = QrDetector.Detect(msg.Text);
        Property? qrProperty = null;
        if (qrShortCode is not null)
        {
            qrProperty = await db.Properties.FirstOrDefaultAsync(p => p.ShortCode == qrShortCode, ct);
            if (qrProperty is not null)
            {
                LeadQualificationEngine.ApplyQrProperty(lead, qrProperty);
                session.Context.QrShortCode = qrShortCode;

                // The lead scanned this exact property's sign: warm personalized
                // greeting + property details + low-pressure visit invitation, all
                // in ONE message (WhatsApp doesn't guarantee ordering across
                // messages, and a separate image reliably arrives after text).
                var card = LeadQualificationEngine.BuildQrWelcome(qrProperty, lead.Name, broker.Name);

                // Card image: the photo grid collage (up to 6 photos in ONE image —
                // the Cloud API can't send albums), falling back to the cover photo.
                // Legacy properties (created before collages) self-heal here: build
                // the collage lazily on first scan.
                var propertyPhotoUrls = await db.PropertyImages
                    .Where(i => i.PropertyId == qrProperty.Id)
                    .OrderBy(i => i.SortOrder)
                    .Select(i => i.Url)
                    .ToListAsync(ct);
                if (qrProperty.CollageUrl is null && propertyPhotoUrls.Count >= 2)
                    qrProperty.CollageUrl = await media.RebuildCollageAsync(qrProperty.Id.ToString("N"), propertyPhotoUrls, ct);
                var cardImage = qrProperty.CollageUrl ?? qrProperty.ImageUrl;
                // WhatsApp image captions cap at ~1024 chars — fall back to text if long.
                if (!string.IsNullOrWhiteSpace(cardImage) && card.Length <= 1000)
                    await sender.SendImageAsync(msg.PhoneNumberId, msg.From, cardImage, card, ct);
                else
                    await sender.SendTextAsync(msg.PhoneNumberId, msg.From, card, ct);

                // The lead must see EVERY photo. With a collage, whatever didn't fit
                // (7th onward) goes individually; without one (single photo), anything
                // beyond the cover goes individually.
                var extraPhotos = qrProperty.CollageUrl is not null
                    ? propertyPhotoUrls.Skip(CollageBuilder.MaxPhotos)
                    : propertyPhotoUrls.Where(u => u != qrProperty.ImageUrl);
                foreach (var photoUrl in extraPhotos)
                    await sender.SendImageAsync(msg.PhoneNumberId, msg.From, photoUrl, "", ct);

                session.Context.History.Add(new TurnRecord { Role = "assistant", Content = card });
                session.Step = LeadSteps.VisitAvailability;
                lead.Status = LeadStatus.Qualifying;
                lead.UpdatedAt = DateTimeOffset.UtcNow;
                session.LastMessageAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(ct);
                return;
            }
            logger.LogWarning("QR code {ShortCode} scanned but no matching property found", qrShortCode);
        }

        // QR follow-up: they were invited to schedule a visit for the scanned
        // property. If they decline it, forget the property's pre-fills and run
        // normal qualification (budget, zone, type...) for the same message.
        var qrDeclined = false;
        if (session.Context.QrShortCode is not null &&
            session.Step == LeadSteps.VisitAvailability &&
            LeadQualificationEngine.IsQrDecline(msg.Text))
        {
            LeadQualificationEngine.ClearQrPrefill(lead);
            session.Context.QrShortCode = null;
            session.Step = LeadSteps.Greeting;
            qrDeclined = true;
        }

        if (qrShortCode is null && !string.IsNullOrWhiteSpace(msg.Text))
        {
            var extraction = await claude.ClassifyAsync(msg.Text, ct);
            if (extraction.IsOffTopic && !qrDeclined)
            {
                await sender.SendTextAsync(msg.PhoneNumberId, msg.From, LeadQualificationEngine.OffTopicReply, ct);
                await db.SaveChangesAsync(ct);
                return;
            }
            if (!extraction.IsOffTopic)
                LeadQualificationEngine.MergeExtraction(lead, extraction);
        }

        // Plan gate on first contact only.
        if (isFirstMessage)
        {
            var leadsThisMonth = await db.Leads.CountAsync(l =>
                l.BrokerId == broker.Id && l.CreatedAt >= DateTimeOffset.UtcNow.AddDays(-30), ct);
            var gate = PlanGate.Check(new PlanGate.Request("new_lead", broker.Plan, LeadsThisMonth: leadsThisMonth));
            if (!gate.Allowed)
            {
                logger.LogWarning("Plan gate blocked new lead for broker {BrokerId}: {Reason}", broker.Id, gate.Reason);
                // Still greet the lead; broker-facing upgrade message is out of band (owner digest / advisor).
            }
        }

        var output = LeadQualificationEngine.Advance(lead, session.Step, broker.Name, isFirstMessage);
        session.Step = output.NextStep;
        lead.Status = output.NewStatus;
        lead.UpdatedAt = DateTimeOffset.UtcNow;

        if (output.ReadyForScoring)
        {
            var scoreResult = LeadScorer.Score(lead);
            lead.Score = scoreResult.Score;

            // The QR property may have been scanned in an earlier message —
            // resolve it from the session so the alert always references it.
            if (qrProperty is null && session.Context.QrShortCode is not null)
                qrProperty = await db.Properties.FirstOrDefaultAsync(p => p.ShortCode == session.Context.QrShortCode, ct);

            // QR + scheduled visit always alerts — the point scale is sale-oriented
            // and would never mark a rental QR lead hot (rent price ≠ $1M budget).
            var alertWorthy = scoreResult.Score >= LeadScorer.HotThreshold ||
                              LeadScorer.IsQrVisitHot(lead, session.Context.QrShortCode);

            // Industry-standard dedup: one alert per (lead, property) — the same
            // person scanning a DIFFERENT property re-alerts the broker; the same
            // property never re-spams. Generic (non-QR) alerts dedup once per lead.
            var alreadyAlerted = await db.LeadAlerts.AnyAsync(a =>
                a.LeadId == lead.Id && a.PropertyId == (qrProperty != null ? qrProperty.Id : null), ct);

            var isHot = alertWorthy && !alreadyAlerted;

            if (isHot)
            {
                lead.AlertSent = true;
                lead.Status = LeadStatus.Hot;
                db.LeadAlerts.Add(new LeadAlert { LeadId = lead.Id, PropertyId = qrProperty?.Id });

                var leadProfile =
                    $"- Name: {lead.Name}\n- Budget: ${lead.BudgetMin}-${lead.BudgetMax} MXN\n" +
                    $"- Zone: {lead.Zone}\n- Type: {lead.PropertyType}\n- Visit: {lead.VisitAvailability}\n" +
                    $"- QR property: {qrProperty?.Title ?? "none"}";
                var summary = AlertBuilder.ConversationSummary(session.Context);
                var pack = await claude.HotLeadPackAsync(leadProfile, summary, broker.Name, ct);
                var alertMessage = AlertBuilder.Build(lead, pack.Coaching, pack.Opener, broker.Name,
                    session.Context.QrShortCode, qrProperty?.Title);

                await sender.SendTextAsync(broker.PhoneNumberId ?? msg.PhoneNumberId, broker.AlertNumber, alertMessage, ct);
                await sender.SendContactCardAsync(broker.PhoneNumberId ?? msg.PhoneNumberId, broker.AlertNumber,
                    lead.Name ?? "Lead", PhoneNumbers.ToDialableMx(lead.Phone), ct);
            }

            // Handoff best practice: announce WHO will contact them (by name) and
            // when, then share the broker's contact card so the lead saves it and
            // recognizes the incoming message instead of distrusting an unknown number.
            string closing;
            if (isHot)
            {
                var leadFirstName = string.IsNullOrWhiteSpace(lead.Name) ? "" : $", {lead.Name.Trim().Split(' ')[0]}";
                closing = $"¡Gracias{leadFirstName}! 🙌 *{broker.Name}* te va a escribir en breve para confirmar tu visita. " +
                          $"Te comparto su contacto para que sepas quién te escribirá 👇";
            }
            else
            {
                closing = "¡Gracias! Un asesor te contactará pronto para coordinar tu visita. 🏠";
            }
            session.Context.History.Add(new TurnRecord { Role = "assistant", Content = closing });
            await sender.SendTextAsync(msg.PhoneNumberId, msg.From, closing, ct);
            if (isHot)
            {
                await sender.SendContactCardAsync(msg.PhoneNumberId, msg.From,
                    broker.Name, PhoneNumbers.ToDialableMx(broker.AlertNumber), ct);
                // Visit confirmed → send the property's exact location pin so the
                // lead knows where to go.
                if (qrProperty is { Latitude: not null, Longitude: not null })
                    await sender.SendLocationAsync(msg.PhoneNumberId, msg.From,
                        qrProperty.Latitude.Value, qrProperty.Longitude.Value,
                        qrProperty.Title, qrProperty.Address ?? qrProperty.Zone, ct);
            }
        }
        else
        {
            var reply = qrDeclined
                ? $"Sin problema 👍 Busquemos otra opción para ti. {output.Reply}"
                : output.Reply;
            session.Context.History.Add(new TurnRecord { Role = "assistant", Content = reply });
            await sender.SendTextAsync(msg.PhoneNumberId, msg.From, reply, ct);
        }

        session.LastMessageAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    // ---------------------------------------------------------------- Broker flow (Workflow 2)

    private async Task HandleBrokerMessageAsync(Broker broker, IncomingMessage msg, CancellationToken ct)
    {
        var session = await GetOrCreateSessionAsync(broker.Id, msg.From, SessionType.Broker, ct);
        var replyPhoneNumberId = broker.PhoneNumberId ?? msg.PhoneNumberId;

        // Active photo-add mode ("fotos CASA-001") takes top priority.
        if (session.Context.PhotoAddShortCode is not null)
        {
            await ContinuePhotoAddAsync(broker, session, msg, replyPhoneNumberId, ct);
            return;
        }

        // Active property intake takes priority over command parsing.
        if (session.Context.BrokerIntake is not null)
        {
            await ContinueIntakeAsync(broker, session, msg, replyPhoneNumberId, ct);
            return;
        }

        var detection = BrokerCommandRouter.Detect(msg.Text);

        switch (detection.Command)
        {
            case BrokerCommandRouter.Command.Ayuda:
                await sender.SendTextAsync(replyPhoneNumberId, msg.From, BrokerCommandRouter.HelpMessage, ct);
                break;

            case BrokerCommandRouter.Command.Fotos:
                if (detection.ShortCode is null)
                {
                    await sender.SendTextAsync(replyPhoneNumberId, msg.From,
                        "¿A qué propiedad? Ejemplo: *fotos CASA-001*", ct);
                    break;
                }
                var photoProperty = await db.Properties.FirstOrDefaultAsync(
                    p => p.BrokerId == broker.Id && p.ShortCode == detection.ShortCode, ct);
                if (photoProperty is null)
                {
                    await sender.SendTextAsync(replyPhoneNumberId, msg.From,
                        $"No encontré la propiedad {detection.ShortCode}.", ct);
                    break;
                }
                session.Context.PhotoAddShortCode = detection.ShortCode;
                await sender.SendTextAsync(replyPhoneNumberId, msg.From,
                    $"📸 Mándame las fotos para *{detection.ShortCode}* (una por una). Escribe *listo* cuando termines.", ct);
                break;

            case BrokerCommandRouter.Command.Agregar:
                var propertiesActive = await db.Properties.CountAsync(p => p.BrokerId == broker.Id && p.Active, ct);
                var gate = PlanGate.Check(new PlanGate.Request("new_property", broker.Plan, PropertiesActive: propertiesActive));
                if (!gate.Allowed)
                {
                    await sender.SendTextAsync(replyPhoneNumberId, msg.From, gate.UpgradeMessage!, ct);
                    break;
                }
                session.Context.BrokerIntake = new BrokerIntakeState();
                await sender.SendTextAsync(replyPhoneNumberId, msg.From,
                    "¡Vamos a agregar una propiedad! ¿Es para *venta*, *renta* o *ambos*?", ct);
                break;

            case BrokerCommandRouter.Command.Listar:
                await HandleListarAsync(broker, replyPhoneNumberId, msg.From, ct);
                break;

            case BrokerCommandRouter.Command.Resumen:
                await HandleResumenAsync(broker, replyPhoneNumberId, msg.From, ct);
                break;

            case BrokerCommandRouter.Command.Pausar:
                await SetPropertyActiveAsync(broker, detection.ShortCode, false, replyPhoneNumberId, msg.From, ct);
                break;

            case BrokerCommandRouter.Command.Activar:
                await SetPropertyActiveAsync(broker, detection.ShortCode, true, replyPhoneNumberId, msg.From, ct);
                break;

            case BrokerCommandRouter.Command.Publicar:
                await HandlePublicarAsync(broker, detection.ShortCode, replyPhoneNumberId, msg.From, ct);
                break;

            case BrokerCommandRouter.Command.Publicidad:
                await HandlePublicidadAsync(broker, detection, replyPhoneNumberId, msg.From, ct);
                break;

            default: // Advisor mode
                var leads7d = await db.Leads.Where(l => l.BrokerId == broker.Id &&
                    l.CreatedAt >= DateTimeOffset.UtcNow.AddDays(-7)).ToListAsync(ct);
                var props = await db.Properties.Where(p => p.BrokerId == broker.Id).ToListAsync(ct);
                var context = BrokerAdvisorContextBuilder.Build(leads7d, props);
                var advice = await claude.AdviseAsync(context, msg.Text ?? "", ct);
                await sender.SendTextAsync(replyPhoneNumberId, msg.From, advice, ct);
                break;
        }

        session.LastMessageAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>"fotos CASA-001" mode: each image appends to the property; "listo" exits.</summary>
    private async Task ContinuePhotoAddAsync(Broker broker, Session session, IncomingMessage msg, string replyPhoneNumberId, CancellationToken ct)
    {
        var shortCode = session.Context.PhotoAddShortCode!;
        var property = await db.Properties.Include(p => p.Images)
            .FirstOrDefaultAsync(p => p.BrokerId == broker.Id && p.ShortCode == shortCode, ct);
        if (property is null)
        {
            session.Context.PhotoAddShortCode = null;
            await sender.SendTextAsync(replyPhoneNumberId, msg.From, $"No encontré la propiedad {shortCode}.", ct);
            await db.SaveChangesAsync(ct);
            return;
        }

        if (!string.IsNullOrEmpty(msg.MediaId))
        {
            var url = await media.DownloadAndStoreAsync(msg.MediaId, ct);
            var nextOrder = property.Images.Count > 0 ? property.Images.Max(i => i.SortOrder) + 1 : 0;
            db.PropertyImages.Add(new PropertyImage { PropertyId = property.Id, Url = url, SortOrder = nextOrder });
            property.ImageUrl ??= url; // first photo ever becomes the cover
            // Silent 📸 reaction per photo — forwarded batches would otherwise
            // trigger one text reply per image.
            await sender.SendReactionAsync(replyPhoneNumberId, msg.From, msg.MessageId, "📸", ct);
        }
        else
        {
            var norm = TextNormalizer.Normalize(msg.Text ?? "");
            if (norm.Contains("listo") || norm.Contains("cancelar") || norm.Contains("ya"))
            {
                session.Context.PhotoAddShortCode = null;
                var allUrls = property.Images.OrderBy(i => i.SortOrder).Select(i => i.Url).ToList();
                property.CollageUrl = await media.RebuildCollageAsync(property.Id.ToString("N"), allUrls, ct);
                var total = property.Images.Count;
                await sender.SendTextAsync(replyPhoneNumberId, msg.From,
                    $"✅ {shortCode} ahora tiene {total} foto{(total == 1 ? "" : "s")}.", ct);
            }
            else
            {
                await sender.SendTextAsync(replyPhoneNumberId, msg.From,
                    $"Manda una foto para {shortCode} o escribe *listo* para terminar.", ct);
            }
        }

        session.LastMessageAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    private async Task ContinueIntakeAsync(Broker broker, Session session, IncomingMessage msg, string replyPhoneNumberId, CancellationToken ct)
    {
        var locationShare = msg.Latitude.HasValue && msg.Longitude.HasValue
            ? new PropertyIntakeStateMachine.LocationShare(msg.Latitude.Value, msg.Longitude.Value, msg.LocationName, msg.LocationAddress)
            : null;
        var result = PropertyIntakeStateMachine.Advance(session.Context.BrokerIntake!, msg.Text, msg.MediaId, locationShare);

        if (result.Cancelled)
        {
            session.Context.BrokerIntake = null;
            await sender.SendTextAsync(replyPhoneNumberId, msg.From, result.Reply ?? "❌ Alta cancelada.", ct);
            await db.SaveChangesAsync(ct);
            return;
        }

        session.Context.BrokerIntake = result.NextState;

        if (!result.Done)
        {
            // Photos in a forwarded batch get a silent 📸 reaction each; text
            // replies only when there's something to say.
            if (result.ReactWithEmoji is not null)
                await sender.SendReactionAsync(replyPhoneNumberId, msg.From, msg.MessageId, result.ReactWithEmoji, ct);
            if (!string.IsNullOrEmpty(result.Reply))
            {
                // The location question ships as an interactive message with the
                // native "send location" button instead of plain text.
                if (result.NextState.Step == IntakeSteps.Location)
                    await sender.SendLocationRequestAsync(replyPhoneNumberId, msg.From, result.Reply, ct);
                else
                    await sender.SendTextAsync(replyPhoneNumberId, msg.From, result.Reply, ct);
            }
            await db.SaveChangesAsync(ct);
            return;
        }

        var data = result.NextState.Data;
        // Upload every photo collected during intake; the first becomes the cover.
        var imageUrls = new List<string>();
        foreach (var mediaId in data.MediaIds)
            imageUrls.Add(await media.DownloadAndStoreAsync(mediaId, ct));
        var imageUrl = imageUrls.FirstOrDefault();

        var kind = ParsePropertyKind(data.Type);
        var listingKind = ParseListingType(data.ListingType);
        var existingCount = await db.Properties.CountAsync(p => p.BrokerId == broker.Id && p.Kind == kind, ct);

        var property = new Property
        {
            BrokerId = broker.Id,
            Title = $"{data.Type} en {data.Zone}",
            Zone = data.Zone,
            Kind = kind,
            ListingKind = listingKind,
            Price = data.Price,
            RentPrice = data.RentPrice,
            Bedrooms = data.Bedrooms,
            Bathrooms = data.Bathrooms,
            Description = data.Description,
            ImageUrl = imageUrl,
            VideoUrl = data.VideoUrl,
            Latitude = data.Latitude,
            Longitude = data.Longitude,
            Address = data.Address,
            ShortCode = ShortCodeGenerator.Generate(data.Type, existingCount),
        };

        // One grid image so the lead's card carries several photos in a single message.
        property.CollageUrl = await media.RebuildCollageAsync(property.Id.ToString("N"), imageUrls, ct);

        db.Properties.Add(property);
        for (var i = 0; i < imageUrls.Count; i++)
            db.PropertyImages.Add(new PropertyImage { PropertyId = property.Id, Url = imageUrls[i], SortOrder = i });
        await SaveWithUniqueRetryAsync(
            () => property.ShortCode = ShortCodeGenerator.Generate(data.Type, ++existingCount),
            ct);

        session.Context.BrokerIntake = null;

        var botNumber = broker.WhatsappNumber ?? appOptions.Value.SharedWhatsAppNumber;
        var qrLink = ShortCodeGenerator.QrLink(botNumber, property.ShortCode!);
        await sender.SendTextAsync(replyPhoneNumberId, msg.From,
            $"✅ Propiedad agregada: *{property.ShortCode}*\n\n" +
            $"📎 Comparte este link para que interesados escaneen o hagan clic:\n{qrLink}\n\n" +
            $"🖨️ QR para imprimir: {ShortCodeGenerator.QrImageUrl(botNumber, property.ShortCode!)}", ct);
    }

    private async Task HandleListarAsync(Broker broker, string replyPhoneNumberId, string to, CancellationToken ct)
    {
        var props = await db.Properties.Where(p => p.BrokerId == broker.Id && p.Active).ToListAsync(ct);
        var text = props.Count > 0
            ? string.Join('\n', props.Select(p => $"• {p.ShortCode}: {p.Title} — ${p.Price ?? p.RentPrice} MXN"))
            : "No tienes propiedades activas.";
        await sender.SendTextAsync(replyPhoneNumberId, to, $"📋 *Tus propiedades:*\n\n{text}", ct);
    }

    private async Task HandleResumenAsync(Broker broker, string replyPhoneNumberId, string to, CancellationToken ct)
    {
        var yesterday = DateTimeOffset.UtcNow.AddDays(-1);
        var leads = await db.Leads.Where(l => l.BrokerId == broker.Id && l.CreatedAt >= yesterday).ToListAsync(ct);
        var digest = DigestBuilder.BuildBrokerDaily(leads, DateTimeOffset.UtcNow)
            ?? "Sin actividad de leads en las últimas 24 horas.";
        await sender.SendTextAsync(replyPhoneNumberId, to, digest, ct);
    }

    private async Task SetPropertyActiveAsync(Broker broker, string? shortCode, bool active, string replyPhoneNumberId, string to, CancellationToken ct)
    {
        var property = await db.Properties.FirstOrDefaultAsync(p => p.BrokerId == broker.Id && p.ShortCode == shortCode, ct);
        if (property is null)
        {
            await sender.SendTextAsync(replyPhoneNumberId, to, $"No encontré la propiedad {shortCode}.", ct);
            return;
        }
        property.Active = active;
        await sender.SendTextAsync(replyPhoneNumberId, to,
            active ? $"🔔 {shortCode} reactivada." : $"🔇 {shortCode} desactivada.", ct);
    }

    private async Task HandlePublicarAsync(Broker broker, string? shortCode, string replyPhoneNumberId, string to, CancellationToken ct)
    {
        var gate = PlanGate.Check(new PlanGate.Request("facebook_post", broker.Plan));
        if (!gate.Allowed)
        {
            await sender.SendTextAsync(replyPhoneNumberId, to, gate.UpgradeMessage!, ct);
            return;
        }

        var property = await db.Properties.FirstOrDefaultAsync(p => p.BrokerId == broker.Id && p.ShortCode == shortCode, ct);
        if (property is null)
        {
            await sender.SendTextAsync(replyPhoneNumberId, to, $"No encontré la propiedad {shortCode}.", ct);
            return;
        }

        var post = await facebook.PostPropertyAsync(property, ct);
        db.AdCampaigns.Add(new AdCampaign
        {
            BrokerId = broker.Id,
            PropertyId = property.Id,
            ShortCode = shortCode,
            FbPostId = post.FbPostId,
            Status = CampaignStatus.Completed,
        });
        await sender.SendTextAsync(replyPhoneNumberId, to, $"📤 Publicado: {post.PostUrl}", ct);
    }

    private async Task HandlePublicidadAsync(Broker broker, BrokerCommandRouter.Detection detection, string replyPhoneNumberId, string to, CancellationToken ct)
    {
        var budget = detection.BudgetMxn ?? 0;
        var adSpent = await db.AdCampaigns
            .Where(c => c.BrokerId == broker.Id && c.CreatedAt >= DateTimeOffset.UtcNow.AddDays(-30))
            .SumAsync(c => c.BudgetMxn ?? 0, ct);

        var gate = PlanGate.Check(new PlanGate.Request("facebook_ad", broker.Plan,
            MonthlyAdBudget: broker.MonthlyAdBudget, AdSpentThisMonth: adSpent, RequestedBudget: budget));
        if (!gate.Allowed)
        {
            await sender.SendTextAsync(replyPhoneNumberId, to, gate.UpgradeMessage!, ct);
            return;
        }

        var property = await db.Properties.FirstOrDefaultAsync(p => p.BrokerId == broker.Id && p.ShortCode == detection.ShortCode, ct);
        if (property is null)
        {
            await sender.SendTextAsync(replyPhoneNumberId, to, $"No encontré la propiedad {detection.ShortCode}.", ct);
            return;
        }

        var post = await facebook.PostPropertyAsync(property, ct);
        var ad = await facebook.CreateAdAsync(property, post.FbPostId, budget, detection.DurationDays, ct);

        db.AdCampaigns.Add(new AdCampaign
        {
            BrokerId = broker.Id,
            PropertyId = property.Id,
            ShortCode = detection.ShortCode,
            FbPostId = post.FbPostId,
            FbCampaignId = ad.CampaignId,
            FbAdsetId = ad.AdSetId,
            FbAdId = ad.AdId,
            DurationDays = detection.DurationDays,
            BudgetMxn = budget,
            BilledMxn = ad.BilledMxn,
            Status = CampaignStatus.Active,
        });

        var confirmation = FacebookService.BuildAdConfirmation(detection.ShortCode!, detection.DurationDays, ad.BilledMxn, post.PostUrl);
        await sender.SendTextAsync(replyPhoneNumberId, to, confirmation, ct);
    }

    // ---------------------------------------------------------------- Persistence helpers

    private async Task<Session> GetOrCreateSessionAsync(Guid brokerId, string phone, SessionType type, CancellationToken ct)
    {
        var existing = await db.Sessions.FirstOrDefaultAsync(s => s.BrokerId == brokerId && s.Phone == phone, ct);
        if (existing is not null) return existing;

        var session = new Session { BrokerId = brokerId, Phone = phone, Type = type };
        db.Sessions.Add(session);
        try
        {
            await db.SaveChangesAsync(ct);
            return session;
        }
        catch (DbUpdateException)
        {
            // Concurrent creation raced us — re-read the row the other request inserted.
            db.Entry(session).State = EntityState.Detached;
            return await db.Sessions.FirstAsync(s => s.BrokerId == brokerId && s.Phone == phone, ct);
        }
    }

    private async Task<Lead> GetOrCreateLeadAsync(Guid brokerId, string phone, CancellationToken ct)
    {
        var existing = await db.Leads.FirstOrDefaultAsync(l => l.BrokerId == brokerId && l.Phone == phone, ct);
        if (existing is not null) return existing;

        var lead = new Lead { BrokerId = brokerId, Phone = phone };
        db.Leads.Add(lead);
        try
        {
            await db.SaveChangesAsync(ct);
            return lead;
        }
        catch (DbUpdateException)
        {
            db.Entry(lead).State = EntityState.Detached;
            return await db.Leads.FirstAsync(l => l.BrokerId == brokerId && l.Phone == phone, ct);
        }
    }

    private async Task SaveWithUniqueRetryAsync(Action regenerateShortCode, CancellationToken ct, int maxAttempts = 5)
    {
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            try
            {
                await db.SaveChangesAsync(ct);
                return;
            }
            catch (DbUpdateException) when (attempt < maxAttempts - 1)
            {
                regenerateShortCode();
            }
        }
    }

    private async Task MarkProcessedAsync(string messageId, CancellationToken ct)
    {
        db.ProcessedMessages.Add(new ProcessedMessage { MessageId = messageId });
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException) { /* another worker already marked it — fine */ }
    }

    private static PropertyKind? ParsePropertyKind(string? type) =>
        type is not null && Enum.TryParse<PropertyKind>(type, true, out var k) ? k : null;

    private static ListingType ParseListingType(string? type) =>
        type is not null && Enum.TryParse<ListingType>(type, true, out var l) ? l : ListingType.Venta;
}
