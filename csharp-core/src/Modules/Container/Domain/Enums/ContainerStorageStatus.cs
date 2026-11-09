namespace NexusPort.Modules.Container.Domain.Enums;

// Native PostgreSQL container_status labels, shared by every physical writer.
public enum ContainerStorageStatus
{
    expected, discharged, in_yard, reserved, moving, gate_in, gate_out, loaded, damaged, canceled
}

public static class ContainerStorageStatusCodec
{
    public static ContainerStorageStatus Parse(string value) => value.Replace("_", "").ToLowerInvariant() switch
    {
        "inyard" or "stacked" => ContainerStorageStatus.in_yard,
        "gatein" => ContainerStorageStatus.gate_in,
        "gateout" => ContainerStorageStatus.gate_out,
        "readyforgateout" => ContainerStorageStatus.moving,
        _ => Enum.Parse<ContainerStorageStatus>(value.ToLowerInvariant())
    };
}
