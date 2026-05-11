using ForgeTrust.RazorWire.TagHelpers;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace ForgeTrust.RazorWire.Tests;

public class LocalTimeTagHelperTests
{
    [Fact]
    public void Process_RendersTimeElementWithDatetimeAttribute()
    {
        var timestamp = new DateTimeOffset(2026, 1, 24, 12, 30, 0, TimeSpan.Zero);
        var helper = new LocalTimeTagHelper
        {
            Value = timestamp,
            RwType = "local"
        };
        var output = CreateOutput();

        helper.Process(CreateContext(), output);

        Assert.Equal("time", output.TagName);
        Assert.Equal("2026-01-24T12:30:00.0000000+00:00", output.Attributes["datetime"].Value);
        Assert.Equal("2026-01-24 12:30:00 UTC", output.Content.GetContent());
    }

    [Fact]
    public void Process_IncludesDataRwLocalTimeAttribute()
    {
        var helper = new LocalTimeTagHelper
        {
            Value = DateTimeOffset.UtcNow,
            RwType = "local"
        };
        var output = CreateOutput();

        helper.Process(CreateContext(), output);

        Assert.True(output.Attributes.ContainsName("data-rw-time"));
    }

    [Fact]
    public void Process_DefaultDisplay_DoesNotRenderDisplayAttribute()
    {
        var helper = new LocalTimeTagHelper
        {
            Value = DateTimeOffset.UtcNow,
            RwType = "local"
        };
        var output = CreateOutput();

        helper.Process(CreateContext(), output);

        Assert.False(output.Attributes.ContainsName("data-rw-time-display"));
    }

    [Fact]
    public void Process_WithDisplayRelative_RendersDisplayAttribute()
    {
        var helper = new LocalTimeTagHelper
        {
            Value = DateTimeOffset.UtcNow,
            RwType = "local",
            Display = "relative"
        };
        var output = CreateOutput();

        helper.Process(CreateContext(), output);

        Assert.Equal("relative", output.Attributes["data-rw-time-display"].Value);
    }

    [Fact]
    public void Process_WithDisplayDatetime_RendersDisplayAttribute()
    {
        var helper = new LocalTimeTagHelper
        {
            Value = DateTimeOffset.UtcNow,
            RwType = "local",
            Display = "datetime"
        };
        var output = CreateOutput();

        helper.Process(CreateContext(), output);

        Assert.Equal("datetime", output.Attributes["data-rw-time-display"].Value);
    }

    [Fact]
    public void Process_WithFormat_RendersFormatAttribute()
    {
        var helper = new LocalTimeTagHelper
        {
            Value = DateTimeOffset.UtcNow,
            RwType = "local",
            Format = "short"
        };
        var output = CreateOutput();

        helper.Process(CreateContext(), output);

        Assert.Equal("short", output.Attributes["data-rw-time-format"].Value);
    }

    [Fact]
    public void Process_WithNullFormat_DoesNotRenderFormatAttribute()
    {
        var helper = new LocalTimeTagHelper
        {
            Value = DateTimeOffset.UtcNow,
            RwType = "local"
        };
        var output = CreateOutput();

        helper.Process(CreateContext(), output);

        Assert.False(output.Attributes.ContainsName("data-rw-time-format"));
    }

    [Fact]
    public void Process_WithDefaultValue_ThrowsArgumentException()
    {
        var helper = new LocalTimeTagHelper { RwType = "local" };
        var output = CreateOutput();

        Assert.Throws<ArgumentException>(() => helper.Process(CreateContext(), output));
    }

    [Fact]
    public void Process_WithInvalidDisplay_ThrowsArgumentException()
    {
        var helper = new LocalTimeTagHelper
        {
            Value = DateTimeOffset.UtcNow,
            RwType = "local",
            Display = "invalid"
        };
        var output = CreateOutput();

        Assert.Throws<ArgumentException>(() => helper.Process(CreateContext(), output));
    }

    [Fact]
    public void Process_WithInvalidFormat_ThrowsArgumentException()
    {
        var helper = new LocalTimeTagHelper
        {
            Value = DateTimeOffset.UtcNow,
            RwType = "local",
            Format = "invalid"
        };
        var output = CreateOutput();

        Assert.Throws<ArgumentException>(() => helper.Process(CreateContext(), output));
    }

    [Fact]
    public void Process_ConvertsLocalTimeToUtc()
    {
        // Create a timestamp with a non-UTC offset
        var localTimestamp = new DateTimeOffset(2026, 1, 24, 12, 30, 0, TimeSpan.FromHours(-5));
        var helper = new LocalTimeTagHelper
        {
            Value = localTimestamp,
            RwType = "local"
        };
        var output = CreateOutput();

        helper.Process(CreateContext(), output);

        // Should be converted to UTC (17:30:00)
        Assert.Equal("2026-01-24T17:30:00.0000000+00:00", output.Attributes["datetime"].Value);
    }

    [Fact]
    public void Process_DisplayIsCaseInsensitive()
    {
        var helper = new LocalTimeTagHelper
        {
            Value = DateTimeOffset.UtcNow,
            RwType = "local",
            Display = "RELATIVE"
        };
        var output = CreateOutput();

        helper.Process(CreateContext(), output);

        Assert.Equal("relative", output.Attributes["data-rw-time-display"].Value);
    }

    [Fact]
    public void Process_RemovesRwTypeAttribute()
    {
        var helper = new LocalTimeTagHelper
        {
            Value = DateTimeOffset.UtcNow,
            RwType = "local"
        };
        var output = CreateOutput();
        output.Attributes.Add("rw-type", "local");

        helper.Process(CreateContext(), output);

        Assert.False(output.Attributes.ContainsName("rw-type"));
    }

    [Fact]
    public void Process_WithUtcType_RendersDataRwTimeAttribute()
    {
        var helper = new LocalTimeTagHelper
        {
            Value = DateTimeOffset.UtcNow,
            RwType = "utc"
        };
        var output = CreateOutput();

        helper.Process(CreateContext(), output);

        Assert.True(output.Attributes.ContainsName("data-rw-time"));
    }

    [Fact]
    public void Process_WithUtcType_EmitsTzAttribute()
    {
        var helper = new LocalTimeTagHelper
        {
            Value = DateTimeOffset.UtcNow,
            RwType = "utc"
        };
        var output = CreateOutput();

        helper.Process(CreateContext(), output);

        Assert.Equal("utc", output.Attributes["data-rw-time-tz"].Value);
    }

    [Fact]
    public void Process_WithLocalType_DoesNotEmitTzAttribute()
    {
        var helper = new LocalTimeTagHelper
        {
            Value = DateTimeOffset.UtcNow,
            RwType = "local"
        };
        var output = CreateOutput();

        helper.Process(CreateContext(), output);

        Assert.False(output.Attributes.ContainsName("data-rw-time-tz"));
    }

    [Fact]
    public void Process_WithUtcType_RemovesRwTypeAttribute()
    {
        var helper = new LocalTimeTagHelper
        {
            Value = DateTimeOffset.UtcNow,
            RwType = "utc"
        };
        var output = CreateOutput();
        output.Attributes.Add("rw-type", "utc");

        helper.Process(CreateContext(), output);

        Assert.False(output.Attributes.ContainsName("rw-type"));
    }

    [Fact]
    public void Process_WithUtcTypeAndDisplay_EmitsBothAttributes()
    {
        var helper = new LocalTimeTagHelper
        {
            Value = DateTimeOffset.UtcNow,
            RwType = "utc",
            Display = "datetime"
        };
        var output = CreateOutput();

        helper.Process(CreateContext(), output);

        Assert.Equal("utc", output.Attributes["data-rw-time-tz"].Value);
        Assert.Equal("datetime", output.Attributes["data-rw-time-display"].Value);
    }

    [Fact]
    public void Process_WithUnknownType_DoesNotProcess()
    {
        var helper = new LocalTimeTagHelper
        {
            Value = DateTimeOffset.UtcNow,
            RwType = "invalid"
        };
        var output = CreateOutput();
        output.Attributes.Add("rw-type", "invalid");

        helper.Process(CreateContext(), output);

        Assert.False(output.Attributes.ContainsName("data-rw-time"));
        Assert.True(output.Attributes.ContainsName("rw-type"));
    }

    private static TagHelperContext CreateContext() =>
        new(new TagHelperAttributeList(), new Dictionary<object, object>(), Guid.NewGuid().ToString());

    private static TagHelperOutput CreateOutput() =>
        new(
            "time",
            new TagHelperAttributeList(),
            (_, _) => Task.FromResult<TagHelperContent>(new DefaultTagHelperContent()));
}
