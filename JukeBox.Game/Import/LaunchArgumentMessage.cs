#nullable enable

using System;

namespace JukeBox.Game.Import;

/// <summary>
/// One second-invocation's arguments, handed to the instance that is already running.
///
/// <para>
/// The WHOLE batch travels as a single message rather than one message per argument. That is what
/// makes queue order fall out for free: the receiving side gets the array in the order it was
/// typed and processes it sequentially, where N separate messages would race down the pipe and
/// arrive in whatever order the listener happened to accept them.
/// </para>
///
/// <para>
/// A class with a settable property, not a record: osu!framework's IPC serialises with
/// Newtonsoft and resolves the payload type by <see cref="Type.AssemblyQualifiedName"/> on the
/// receiving side, so this must be a plain deserialisable shape living in an assembly BOTH
/// processes load. JukeBox.Game qualifies; it is also the only assembly the test project
/// references, which is why the type lives here rather than beside the entry point that sends it.
/// </para>
/// </summary>
public class LaunchArgumentMessage
{
    public string[] Arguments { get; set; } = Array.Empty<string>();
}
