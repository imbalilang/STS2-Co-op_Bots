using System.Reflection;

namespace CoopBots.Kernel.Vendor.Engine.Common.Mirrors;

public enum MethodMirrorRegistrationKind
{
    Handled,
    Ignored,
}

public sealed record MethodMirrorRegistrationDescriptor(
    Type ReceiverType,
    MethodMirrorRegistrationKind Kind);

public sealed record MethodMirrorRegistryDescriptor(
    Type ReceiverType,
    MethodInfo BaseMethod,
    IReadOnlyList<MethodMirrorRegistrationDescriptor> Registrations,
    Delegate? StrictInferrer,
    Delegate? Inferrer);

public interface IMethodMirrorRegistryDescriptorProvider
{
    MethodMirrorRegistryDescriptor DescribeMirrorSupport();
}
