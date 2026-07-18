namespace KKR.MailLens;

static class CommandLineSecurity
{
    public static bool ContainsOption(IEnumerable<string> arguments, string name)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return arguments.Any(argument => argument.Equals(name, StringComparison.OrdinalIgnoreCase)
            || argument.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase));
    }
}
