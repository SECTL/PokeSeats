using PokeSeats.UI.PluginContracts;

namespace PokeSeats.UI.Betapayload;

public sealed class BetapayloadPlugin : IUiPlugin
{
    public void Initialize(IPluginContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Beta features register their views and commands here, separately from DefaultPayload.
    }

    public void Dispose()
    {
        // No plugin-owned resources yet.
    }
}
