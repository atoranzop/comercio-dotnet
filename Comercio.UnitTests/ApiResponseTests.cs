using System.Text.Json;
using Comercio.Shared;

namespace Comercio.UnitTests;

public sealed class ApiResponseTests
{
    private static readonly JsonSerializerOptions WebJsonOptions =
        new(JsonSerializerDefaults.Web);

    [Fact]
    public void SuccessfulResponse_RoundTripsThroughJson()
    {
        var original = ApiResponse<string>.Succeeded("created");

        var json = JsonSerializer.Serialize(original, WebJsonOptions);
        var deserialized = JsonSerializer.Deserialize<ApiResponse<string>>(
            json,
            WebJsonOptions);

        Assert.NotNull(deserialized);
        Assert.True(deserialized.Success);
        Assert.Equal("created", deserialized.Data);
        Assert.Null(deserialized.Error);
    }

    [Fact]
    public void ErrorResponse_RoundTripsThroughJson()
    {
        var original = ApiResponse<string>.Failed(
            "validation_failed",
            "The request is invalid.",
            new Dictionary<string, string[]>
            {
                ["name"] = ["Name is required."]
            },
            "trace-123");

        var json = JsonSerializer.Serialize(original, WebJsonOptions);
        var deserialized = JsonSerializer.Deserialize<ApiResponse<string>>(
            json,
            WebJsonOptions);

        Assert.NotNull(deserialized);
        Assert.False(deserialized.Success);
        Assert.Null(deserialized.Data);
        Assert.NotNull(deserialized.Error);
        Assert.Equal("validation_failed", deserialized.Error.Code);
        Assert.Equal("The request is invalid.", deserialized.Error.Message);
        Assert.Equal("Name is required.", Assert.Single(deserialized.Error.Details!["name"]));
        Assert.Equal("trace-123", deserialized.Error.TraceId);
    }
}
