using System.Reflection;
using FluentAssertions;
using UavOps.Agent.Hubs;
using UavOps.FleetClient;
using Contracts = UavOps.Agent.Contracts;

namespace UavOps.Agent.Tests;

/// <summary>
/// The host's fleet wire contract (<see cref="IOperationClientProxy"/> plus the Contracts models)
/// and UavOps.FleetClient's copy of it (<see cref="IUavCommandHandler"/>,
/// <see cref="IUavMissionHandler"/>, its Models.cs) are separate code in separate TFMs, matched
/// only by name. Nothing else fails when they drift: a renamed or retyped command just times out
/// at runtime, and a renamed model property silently deserializes as null/0.
/// FleetClientConnection registers each command under nameof(its handler method), so checking
/// the handler interfaces here covers the registered names too.
/// </summary>
public class FleetContractDriftTests
{
    private static readonly Type[] HandlerInterfaces = [typeof(IUavCommandHandler), typeof(IUavMissionHandler)];

    [Fact]
    public void EveryHostCommand_HasAFleetClientHandlerWithTheSameArguments()
    {
        var handlers = HandlerInterfaces.SelectMany(i => i.GetMethods()).ToDictionary(m => m.Name);

        foreach (var command in typeof(IOperationClientProxy).GetMethods())
        {
            handlers.Should().ContainKey(command.Name, $"the host sends '{command.Name}' to the fleet app");

            var parameters = command.GetParameters();
            parameters[0].Name.Should().Be("correlationId");

            Shapes(parameters.Skip(1)).Should().Equal(
                Shapes(handlers[command.Name].GetParameters()),
                $"'{command.Name}' must carry the same arguments on both sides of the wire");
        }
    }

    [Fact]
    public void EveryFleetClientHandler_IsACommandTheHostSends()
    {
        var commands = typeof(IOperationClientProxy).GetMethods().Select(m => m.Name).ToHashSet();

        HandlerInterfaces.SelectMany(i => i.GetMethods()).Select(m => m.Name)
            .Should().OnlyContain(name => commands.Contains(name));
    }

    [Theory]
    [InlineData(typeof(Contracts.Waypoint), typeof(Waypoint))]
    [InlineData(typeof(Contracts.TelemetrySnapshot), typeof(TelemetrySnapshot))]
    [InlineData(typeof(Contracts.GdtLinkStatus), typeof(GdtLinkStatus))]
    [InlineData(typeof(Contracts.UavSummary), typeof(UavSummary))]
    [InlineData(typeof(Contracts.MissionStatus), typeof(MissionStatus))]
    [InlineData(typeof(Contracts.SearchTargetRequest), typeof(SearchTargetRequest))]
    [InlineData(typeof(Contracts.DetectionReport), typeof(DetectionReport))]
    [InlineData(typeof(Contracts.MissionEventReport), typeof(MissionEventReport))]
    public void WireModel_HasTheSamePropertiesOnBothSides(Type host, Type fleetClient)
    {
        Properties(fleetClient).Should().BeEquivalentTo(Properties(host));
    }

    /// <summary>The fleet app's own calls into the host. FleetClientConnection invokes these by
    /// name string (it can't reference the host), so the names are listed here from that file.</summary>
    [Theory]
    [InlineData("ReportDetection", typeof(DetectionReport))]
    [InlineData("ReportMissionEvent", typeof(MissionEventReport))]
    public void EveryFleetAppCall_HasAMatchingHubMethod(string name, Type fleetClientArgument)
    {
        var method = typeof(ChatHub).GetMethod(name);

        method.Should().NotBeNull($"FleetClientConnection invokes '{name}' on the host");
        Shapes(method!.GetParameters()).Should().Equal(Shape(fleetClientArgument));
    }

    [Fact]
    public void MissionEventKinds_MatchOnBothSides()
    {
        new[] { MissionEventKinds.Completed, MissionEventKinds.Aborted }.Should().Equal(
            Contracts.MissionEventKinds.Completed, Contracts.MissionEventKinds.Aborted);
    }

    private static Dictionary<string, string> Properties(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.Name != "EqualityContract")
            .ToDictionary(p => p.Name.ToLowerInvariant(), p => Shape(p.PropertyType));

    private static IEnumerable<string> Shapes(IEnumerable<ParameterInfo> parameters) =>
        parameters.Select(p => Shape(p.ParameterType));

    // Compares types by name, since each side has its own copy of every model type.
    private static string Shape(Type type)
    {
        if (Nullable.GetUnderlyingType(type) is { } underlying)
            return Shape(underlying) + "?";
        if (!type.IsGenericType)
            return type.Name;
        return $"{type.Name}<{string.Join(",", type.GetGenericArguments().Select(Shape))}>";
    }
}
