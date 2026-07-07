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
                // WhatsApp image captions cap at ~1024 chars — fall back to text if long.
                if (!string.IsNullOrWhiteSpace(qrProperty.ImageUrl) && card.Length <= 1000)
                    await sender.SendImageAsync(msg.PhoneNumberId, msg.From, qrProperty.ImageUrl, card, ct);
                else
                    await sender.SendTextAsync(msg.PhoneNumberId, msg.From, card, ct);

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

            // QR + scheduled visit always alerts — the point scale is sale-oriented
            // and would never mark a rental QR lead hot (rent price ≠ $1M budget).
            var isHot = scoreResult.IsHot || LeadScorer.IsQrVisitHot(lead, session.Context.QrShortCode);

            if (isHot)
            {
                lead.AlertSent = true;
                lead.Status = LeadStatus.Hot;

                var leadProfile =
                    $"- Name: {lead.Name}\n- Budget: ${lead.BudgetMin}-${lead.BudgetMax} MXN\n" +
                    $"- Zone: {lead.Zone}\n- Type: {lead.PropertyType}\n- Visit: {lead.VisitAvailability}\n" +
                    $"- QR property: {qrProperty?.Title ?? "none"}";
                var summary = AlertBuilder.ConversationSummary(session.Context);
                var coaching = await claude.SellingArgumentsAsync(leadProfile, summary, ct);
                var alertMessage = AlertBuilder.Build(lead, coaching, session.Context.QrShortCode, qrProperty?.Title);

                await sender.SendTextAsync(broker.PhoneNumberId ?? msg.PhoneNumberId, broker.AlertNumber, alertMessage, ct);
                await sender.SendContactCardAsync(broker.PhoneNumberId ?? msg.PhoneNumberId, broker.AlertNumber, lead.Name ?? "Lead", lead.Phone, ct);
            }

            var closing = "¡Gracias! Un asesor te contactará pronto para coordinar tu visita. 🏠";
            session.Context.History.Add(new TurnRecord { Role = "assistant", Content = closing });
            await sender.SendTextAsync(msg.PhoneNumberId, msg.From, closing, ct);
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

    private async Task ContinueIntakeAsync(Broker broker, Session session, IncomingMessage msg, string replyPhoneNumberId, CancellationToken ct)
    {
        var result = PropertyIntakeStateMachine.Advance(session.Context.BrokerIntake!, msg.Text, msg.MediaId);
        session.Context.BrokerIntake = result.NextState;

        if (!result.Done)
        {
            await sender.SendTextAsync(replyPhoneNumberId, msg.From, result.Reply ?? "", ct);
            return;
        }

        var data = result.NextState.Data;
        string? imageUrl = null;
        if (!string.IsNullOrEmpty(data.MediaId))
            imageUrl = await media.DownloadAndStoreAsync(data.MediaId, ct);

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
            ShortCode = ShortCodeGenerator.Generate(data.Type, existingCount),
        };

        db.Properties.Add(property);
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
