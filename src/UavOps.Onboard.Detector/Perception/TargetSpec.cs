namespace UavOps.Onboard.Detector.Perception;

/// <summary>The detector's classes this service cares about (Objects365 names, as D-FINE's
/// <c>id2label</c> spells them), and which of them are vehicles.</summary>
public static class ObjectClasses
{
    public static readonly string[] Vehicles = ["Car", "SUV", "Van", "Pickup Truck", "Truck", "Heavy Truck", "Fire Truck", "Bus", "Sports Car", "Motorcycle"];

    public static bool IsVehicle(string label) => Vehicles.Contains(label, StringComparer.OrdinalIgnoreCase);

    /// <summary>Classes worth tracking at all: vehicles and people. Everything else the detector
    /// knows (365 classes: chairs, lamps...) is noise from 1,000 ft.</summary>
    public static bool IsTracked(string label) => IsVehicle(label) || label.Equals("Person", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The search target, made structured once per search: which detector classes can be it and what
/// colour it must look. The detector and the tracker only ever propose candidates by these; whether
/// a candidate really is the target ("a van" or a minibus?) is the verifier's call, on a close-up.
/// Deliberately broad: from above, a van, an SUV and a pickup are easily confused frame to frame,
/// so a "white van" search watches every white car-like thing and lets the verifier decide.
/// </summary>
public sealed record TargetSpec(string Prompt, IReadOnlySet<string> Classes, IReadOnlySet<string> Colours)
{
    private static readonly Dictionary<string, string[]> KindClasses = new(StringComparer.Ordinal)
    {
        ["car"] = ["Car", "SUV", "Sports Car", "Van"],
        ["sedan"] = ["Car", "Sports Car"],
        ["hatchback"] = ["Car"],
        ["taxi"] = ["Car", "Van"],
        ["suv"] = ["SUV", "Car", "Van"],
        ["jeep"] = ["SUV", "Car", "Pickup Truck"],
        ["van"] = ["Van", "Car", "SUV", "Truck"],
        ["minivan"] = ["Van", "Car", "SUV"],
        ["pickup"] = ["Pickup Truck", "Car", "SUV", "Truck"],
        ["truck"] = ["Truck", "Heavy Truck", "Pickup Truck", "Fire Truck", "Van"],
        ["lorry"] = ["Truck", "Heavy Truck"],
        ["bus"] = ["Bus", "Truck", "Heavy Truck"],
        ["motorcycle"] = ["Motorcycle"],
        ["motorbike"] = ["Motorcycle"],
        ["vehicle"] = ObjectClasses.Vehicles,
        ["person"] = ["Person"],
        ["people"] = ["Person"],
        ["man"] = ["Person"],
        ["woman"] = ["Person"],
    };

    /// <summary>Colour words, and the colour names (<see cref="ColourNamer"/>) each accepts: silver and
    /// grey read the same from above, and a white roof in shade can read light grey.</summary>
    private static readonly Dictionary<string, string[]> ColourNames = new(StringComparer.Ordinal)
    {
        ["white"] = ["white", "gray"],
        ["black"] = ["black"],
        ["gray"] = ["gray", "white", "black"],
        ["grey"] = ["gray", "white", "black"],
        ["silver"] = ["gray", "white"],
        ["red"] = ["red", "orange", "brown"], // a red roof in shade reads brown (measured on the red car)
        ["blue"] = ["blue"],
        ["green"] = ["green"],
        ["yellow"] = ["yellow", "orange"],
        ["orange"] = ["orange", "red", "yellow"],
        ["brown"] = ["brown", "orange"],
        ["beige"] = ["brown", "white", "yellow"],
    };

    /// <summary>Null when the prompt names nothing the detector knows (a pylon, a bridge): that
    /// search stays with the vision-model pipeline.</summary>
    public static TargetSpec? Parse(string prompt)
    {
        var words = prompt.ToLowerInvariant()
            .Split([' ', ',', '.', '-', '/', '\t', '\n', '(', ')', '"', '\''], StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w == "pick" ? "pickup" : w)
            .Select(w => w.Length > 3 && w.EndsWith('s') && !w.EndsWith("ss") && !w.EndsWith("us") ? w[..^1] : w)
            .ToList();
        var classes = words.Where(KindClasses.ContainsKey).SelectMany(w => KindClasses[w]).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (classes.Count == 0)
            return null;
        var colours = words.Where(ColourNames.ContainsKey).SelectMany(w => ColourNames[w]).ToHashSet(StringComparer.Ordinal);
        return new TargetSpec(prompt, classes, colours);
    }

    /// <summary>Whether a track could be the target: its class is one of the target's, and its
    /// colour (when the target names one) is one the target accepts.</summary>
    public bool CouldBe(string detectedClass, string? colour) =>
        Classes.Contains(detectedClass) && ColourFits(colour);

    /// <summary>Whether a sighting could be the target once it has been verified and locked on: for
    /// a vehicle, any vehicle class of the target's colour. Measured on the Jetson: zoomed in on the
    /// locked red car, the detector called it "Truck" and "Bus" as often as "Car", 0.1 m from where
    /// it should be; by class alone those were rejected and the car was lost in plain view.</summary>
    public bool CouldStillBe(string detectedClass, string? colour) =>
        Classes.Any(ObjectClasses.IsVehicle)
            ? ObjectClasses.IsVehicle(detectedClass) && ColourFits(colour)
            : CouldBe(detectedClass, colour);

    private bool ColourFits(string? colour) => Colours.Count == 0 || (colour is not null && Colours.Contains(colour));
}
