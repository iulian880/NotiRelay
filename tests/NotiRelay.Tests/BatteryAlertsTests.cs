using NotiRelay;
using Xunit;

public class BatteryAlertsTests
{
    [Fact]
    public void Discharging_AlertsOnceAt30AndOnceAt10()
    {
        var alerts = new BatteryAlerts();

        Assert.Null(alerts.Update(100, onBattery: true));
        Assert.Null(alerts.Update(31, onBattery: true));

        BatteryAlert? low = alerts.Update(30, onBattery: true);
        Assert.Equal(30, low?.Threshold);
        Assert.Equal("Laptop battery low: 30%", low?.Title);
        Assert.Equal("high", low?.Priority);

        Assert.Null(alerts.Update(29, onBattery: true));
        Assert.Null(alerts.Update(11, onBattery: true));

        BatteryAlert? critical = alerts.Update(10, onBattery: true);
        Assert.Equal(10, critical?.Threshold);
        Assert.Equal("Laptop battery critical: 10%", critical?.Title);
        Assert.Equal("urgent", critical?.Priority);

        Assert.Null(alerts.Update(5, onBattery: true));
    }

    [Fact]
    public void JitterAroundTheThreshold_DoesNotRepeat()
    {
        var alerts = new BatteryAlerts();

        Assert.NotNull(alerts.Update(30, onBattery: true));
        Assert.Null(alerts.Update(31, onBattery: true));
        Assert.Null(alerts.Update(30, onBattery: true));
        Assert.Null(alerts.Update(33, onBattery: true));
        Assert.Null(alerts.Update(30, onBattery: true));
    }

    [Fact]
    public void StartingBelow10_SendsOnlyThe10Alert()
    {
        var alerts = new BatteryAlerts();

        Assert.Equal(10, alerts.Update(8, onBattery: true)?.Threshold);
        Assert.Null(alerts.Update(7, onBattery: true));
    }

    [Fact]
    public void PluggedIn_NeverAlerts_AndChargingAbove35ReArms30()
    {
        var alerts = new BatteryAlerts();

        Assert.Null(alerts.Update(20, onBattery: false));
        Assert.Equal(30, alerts.Update(20, onBattery: true)?.Threshold);

        // Charged a little, unplugged again: still the same low episode.
        Assert.Null(alerts.Update(33, onBattery: false));
        Assert.Null(alerts.Update(32, onBattery: true));

        // Charged well above 30: a new drop to 30 alerts again.
        Assert.Null(alerts.Update(40, onBattery: false));
        Assert.Equal(30, alerts.Update(30, onBattery: true)?.Threshold);
    }

    [Fact]
    public void ImpossibleReadings_AreIgnored()
    {
        var alerts = new BatteryAlerts();

        Assert.Null(alerts.Update(-1, onBattery: true));
        Assert.Null(alerts.Update(255, onBattery: true));
    }

    [Fact]
    public void AlertBody_SaysToPlugIn()
    {
        var alerts = new BatteryAlerts();

        Assert.Equal("Not charging. Plug in soon.", alerts.Update(28, onBattery: true)?.Body);
        Assert.Contains("Plug in now", alerts.Update(9, onBattery: true)?.Body);
    }
}
