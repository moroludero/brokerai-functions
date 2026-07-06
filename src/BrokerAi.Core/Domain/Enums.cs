namespace BrokerAi.Core.Domain;

public enum PlanTier { Basico, Pro, Agencia }

public enum LeadStatus { New, Qualifying, Qualified, Hot, Closed }

public enum SessionType { Lead, Broker }

public enum LeadGoal { Comprar, Rentar }

public enum PropertyKind { Casa, Depto, Terreno, Comercial }

public enum ListingType { Venta, Renta, Ambos }

public enum CampaignStatus { Active, Completed, Cancelled, PendingBilling }

/// <summary>Lead qualification steps (sessions.step). Broker intake steps live inside SessionContext.BrokerIntake.</summary>
public static class LeadSteps
{
    public const string Greeting = "greeting";
    public const string ListingTypeStep = "listing_type";
    public const string Budget = "budget";
    public const string Zone = "zone";
    public const string PropertyType = "property_type";
    public const string VisitAvailability = "visit_availability";
    public const string Qualified = "qualified";
}

public static class Zones
{
    public const string CancunCentro = "Cancún Centro";
    public const string ZonaHotelera = "Zona Hotelera";
    public const string PlayaDelCarmen = "Playa del Carmen";
    public const string Tulum = "Tulum";
    public const string PuertoMorelos = "Puerto Morelos";

    public static readonly string[] All =
        [CancunCentro, ZonaHotelera, PlayaDelCarmen, Tulum, PuertoMorelos];
}
