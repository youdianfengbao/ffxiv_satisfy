using Lumina.Excel.Sheets;
using System.Numerics;

namespace Satisfy;

// data & functions needed to fish an item
public sealed class FishData
{
    public uint FishItemId;
    public bool IsSpearFish;
    public uint FishSpotId;
    public uint TerritoryTypeId;
    public Vector3 Center;
    public int Radius; // TODO: no idea what scale it uses?..

    public FishData(uint itemId)
    {
        FishItemId = itemId;

        // 尝试从 FishingSpot 表查找
        var fishingSpots = Service.LuminaSheet<FishingSpot>();
        if (fishingSpots != null)
        {
            var fish = fishingSpots.FirstOrDefault(s => s.Item.Any(i => i.RowId == FishItemId));
            if (fish.RowId != 0)
            {
                FishSpotId = fish.RowId;
                TerritoryTypeId = fish.TerritoryType.RowId;
                Center = Map.PixelCoordsToWorldCoords(fish.X, fish.Z, fish.TerritoryType.Value.Map.RowId);
                Radius = fish.Radius;
                return;
            }
        }

        // 尝试从 SpearfishingItem 表查找
        var spearItems = Service.LuminaSheet<SpearfishingItem>();
        if (spearItems != null)
        {
            var sfish = spearItems.FirstOrDefault(s => s.Item.RowId == FishItemId);
            if (sfish.RowId != 0)
            {
                IsSpearFish = true;
                FishSpotId = sfish.TerritoryType.RowId;
                var fishSpot = Service.LuminaRow<SpearfishingNotebook>(FishSpotId);
                if (fishSpot != null)
                {
                    var spot = fishSpot.Value;
                    TerritoryTypeId = spot.TerritoryType.RowId;
                    Center = Map.PixelCoordsToWorldCoords(spot.X, spot.Y, spot.TerritoryType.Value.Map.RowId);
                    Radius = spot.Radius;
                    return;
                }
            }
        }

        throw new Exception($"Failed to find fishing location for {itemId}");
    }
}
