using SwiftlyS2.Core.Natives;
using SwiftlyS2.Core.Players;
using SwiftlyS2.Shared.Players;
using SwiftlyS2.Shared.SchemaDefinitions;

namespace SwiftlyS2.Core.SchemaDefinitions;

internal partial class CBasePlayerControllerImpl : CBasePlayerController
{
    public void SetPawn( CBasePlayerPawn? pawn )
    {
        var handle = pawn?.Address;
        GameFunctions.SetPlayerControllerPawn(Address, handle ?? IntPtr.Zero, true, false, false, false);
    }

    public IPlayer? ToPlayer()
    {
        if (!IsValid) return null;
        var slots = PlayerManagerService.PlayerObjects;
        var idx = (int)(Index - 1);
        var player = (uint)idx < (uint)slots.Length ? slots[idx] : null;
        if (player == null) return null;

        return player is { IsValid: true } ? player : null;
    }
}