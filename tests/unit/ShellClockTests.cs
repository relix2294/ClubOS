using ClubOS.Agent.Core.PlayerShell;
using ClubOS.Contracts;
using Xunit;

namespace ClubOS.Unit.Tests;

/// <summary>Индикатор сессии: остаток, стоимость (тот же BillingCalculator), предупреждения, PIN техника, клавиши.</summary>
public class ShellClockTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

    private static ShellSession Session(int? limitMinutes) => new()
    {
        SessionId = "ses_1",
        StartedAtUtc = Start,
        PlannedEndAtUtc = limitMinutes is { } m ? Start.AddMinutes(m) : null,
        PriceSnapshot = new PriceSnapshot
        {
            PricePerHourMinorUnits = 12_000,
            Currency = "TJS",
            Rounding = RoundingRule.CeilingPerMinute,
            RuleVersion = 1
        }
    };

    [Theory]
    [InlineData(60, 200)]
    [InlineData(61, 400)]
    [InlineData(1800, 6_000)]
    public void Cost_matches_edge_billing(int seconds, long expected) =>
        Assert.Equal(expected, ShellClock.CurrentCost(Session(null), Start.AddSeconds(seconds)));

    [Fact]
    public void Cost_stops_at_planned_end()
    {
        var session = Session(30);
        Assert.Equal(6_000, ShellClock.CurrentCost(session, Start.AddMinutes(45)));
        Assert.Equal(TimeSpan.Zero, ShellClock.Remaining(session, Start.AddMinutes(45)));
        Assert.Equal(TimeSpan.FromMinutes(30), ShellClock.Elapsed(session, Start.AddMinutes(45)));
    }

    [Fact]
    public void Open_session_has_no_remaining_and_no_warning()
    {
        var session = Session(null);
        Assert.Null(ShellClock.Remaining(session, Start.AddHours(5)));
        Assert.Equal(ShellWarning.None, ShellClock.Warning(session, Start.AddHours(5)));
        Assert.Equal("Идёт 5:00:00", ShellText.Hud(session, Start.AddHours(5)).Time);
    }

    [Theory]
    [InlineData(0, ShellWarning.None)]
    [InlineData(24 * 60, ShellWarning.None)]
    [InlineData(25 * 60, ShellWarning.Soon)]
    [InlineData(29 * 60, ShellWarning.Critical)]
    [InlineData(30 * 60, ShellWarning.Critical)]
    public void Warnings_at_5_and_1_minute(int elapsedSeconds, ShellWarning expected) =>
        Assert.Equal(expected, ShellClock.Warning(Session(30), Start.AddSeconds(elapsedSeconds)));

    [Fact]
    public void Hud_text_for_limited_session()
    {
        var text = ShellText.Hud(Session(90), Start.AddMinutes(20).AddSeconds(5));
        Assert.Equal("Осталось 1:09:55", text.Time);
        Assert.Equal("Стоимость 42,00 TJS", text.Cost);
        Assert.Null(text.Warning);

        var critical = ShellText.Hud(Session(90), Start.AddMinutes(89).AddSeconds(30));
        Assert.Equal("Осталось 00:30", critical.Time);
        Assert.NotNull(critical.Warning);
    }

    [Theory]
    [InlineData(0, "00:00")]
    [InlineData(59, "00:59")]
    [InlineData(3599, "59:59")]
    [InlineData(3600, "1:00:00")]
    [InlineData(90061, "25:01:01")]
    public void Duration_format(int seconds, string expected) =>
        Assert.Equal(expected, ShellClock.FormatDuration(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void End_reason_texts()
    {
        Assert.Equal("Оплаченное время закончилось", ShellText.EndReason(SessionEndReasons.TimeLimit));
        Assert.Equal("Сессию завершил администратор", ShellText.EndReason(SessionEndReasons.Staff));
        Assert.Equal("Сессия закрыта", ShellText.EndReason(null));
    }

    [Fact]
    public void Technician_pin_hash_verifies_only_the_right_pin()
    {
        var hash = TechnicianPin.Hash("123456");
        Assert.StartsWith("pbkdf2-sha256$", hash);
        Assert.DoesNotContain("123456", hash);
        Assert.True(TechnicianPin.Verify(hash, "123456"));
        Assert.False(TechnicianPin.Verify(hash, "123457"));
        Assert.False(TechnicianPin.Verify(hash, ""));
        Assert.NotEqual(hash, TechnicianPin.Hash("123456")); // соль
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("plain")]
    [InlineData("pbkdf2-sha256$1$AAAA$BBBB")]
    [InlineData("pbkdf2-sha256$999999999$AAAAAAAAAAAAAAAAAAAAAA==$AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")]
    [InlineData("pbkdf2-sha256$210000$!!!$???")]
    public void Malformed_pin_hash_never_verifies(string? hash) => Assert.False(TechnicianPin.Verify(hash, "123456"));

    [Theory]
    [InlineData("12345")]
    [InlineData("1234567890123")]
    [InlineData("12ab56")]
    public void Pin_format_is_validated(string pin)
    {
        Assert.NotNull(TechnicianPin.ValidateFormat(pin));
        Assert.Throws<ArgumentException>(() => TechnicianPin.Hash(pin));
    }

    [Theory]
    [InlineData(ShellKeyFilter.VkLWin, false, false, true)]
    [InlineData(ShellKeyFilter.VkRWin, false, false, true)]
    [InlineData(ShellKeyFilter.VkApps, false, false, true)]
    [InlineData(ShellKeyFilter.VkTab, true, false, true)]
    [InlineData(ShellKeyFilter.VkTab, false, false, false)]
    [InlineData(ShellKeyFilter.VkEscape, false, true, true)]
    [InlineData(ShellKeyFilter.VkEscape, true, false, true)]
    [InlineData(ShellKeyFilter.VkEscape, false, false, false)]
    [InlineData(ShellKeyFilter.VkF4, true, false, true)]
    [InlineData(ShellKeyFilter.VkF12, false, true, false)] // Ctrl+Shift+F12 — вход техника
    [InlineData(0x41, false, false, false)]              // обычная клавиша (A) — для PIN-диалога
    public void Key_filter(int vk, bool alt, bool ctrl, bool blocked) =>
        Assert.Equal(blocked, ShellKeyFilter.ShouldBlock(vk, alt, ctrl));
}
