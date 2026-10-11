namespace RetailFlow.Application;

/// <summary>
/// Anchor type for assembly scanning. Registration of use-case handlers, validators and decorators
/// (ADR-0015) resolves this assembly rather than hard-coding its name as a string.
/// </summary>
public static class ApplicationAssemblyMarker
{
    public static System.Reflection.Assembly Assembly => typeof(ApplicationAssemblyMarker).Assembly;
}
