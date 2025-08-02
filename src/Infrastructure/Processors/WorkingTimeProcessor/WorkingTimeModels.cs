using System.Text.Json.Serialization;

namespace ChatbotApi.Infrastructure.Processors.WorkingTimeProcessor;

public enum WorkingTimeType
{
    CheckIn,
    CheckOut
}

public enum WorkingTimeStep
{
    WaitingForLocation,
    WaitingForOfficeSelection,
    WaitingForPhoto
}

public class WorkingTimeSession
{
    public string UserId { get; set; }
    public WorkingTimeType Type { get; set; }
    public WorkingTimeStep Step { get; set; }
    public DateTime CreatedAt { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public string? Address { get; set; }
    public string? SelectedOfficeName { get; set; }
    public string? SelectedOfficePlaceId { get; set; }
    public double? SelectedOfficeLatitude { get; set; }
    public double? SelectedOfficeLongitude { get; set; }
    public byte[]? PhotoContent { get; set; }
    public string? PhotoContentType { get; set; }
}

public class GovernmentOffice
{
    public string Name { get; set; }
    public string PlaceId { get; set; }
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public string? Address { get; set; }
}

public class WorkingTimeData
{
    public string AgencyName { get; set; }
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public DateTime Timestamp { get; set; }
    public WorkingTimeType ActionType { get; set; }
    public byte[] Photo { get; set; }
    public string LineUserId { get; set; }
}

public class GooglePlacesResponse
{
    [JsonPropertyName("results")]
    public List<GooglePlaceResult> Results { get; set; }
}

public class GooglePlaceResult
{
    [JsonPropertyName("place_id")]
    public string PlaceId { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; }

    [JsonPropertyName("geometry")]
    public GooglePlaceGeometry Geometry { get; set; }

    [JsonPropertyName("vicinity")]
    public string? Vicinity { get; set; }
}

public class GooglePlaceGeometry
{
    [JsonPropertyName("location")]
    public GooglePlaceLocation Location { get; set; }
}

public class GooglePlaceLocation
{
    [JsonPropertyName("lat")]
    public double Latitude { get; set; }

    [JsonPropertyName("lng")]
    public double Longitude { get; set; }
}

public class HrSystemCheckInCheckOutRequest
{
    public string UserId { get; set; }
    public string LatLong { get; set; }
    public string Location { get; set; }
    public string IpAddress { get; set; }
    public string OrganizationName { get; set; }
    public string ProjectName { get; set; }
    public string FileName { get; set; }
    public string Base64 { get; set; }
}