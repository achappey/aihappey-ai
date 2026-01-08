namespace AIHappey.Common.Model.Providers.OpenAI;

/// <summary>
/// Holds either a string or <see cref="CodeInterpreterContainer"/>.
/// </summary>
public readonly struct ContainerUnion
{
    public string? String { get; }
    public CodeInterpreterContainer? Object { get; }

    public bool IsString => String is not null;
    public bool IsObject => Object is not null;

    public ContainerUnion(string value)
    {
        String = value;
        Object = null;
    }

    public ContainerUnion(CodeInterpreterContainer value)
    {
        Object = value;
        String = null;
    }

    public static implicit operator ContainerUnion(string value) => new(value);
    public static implicit operator ContainerUnion(CodeInterpreterContainer value) => new(value);
}

