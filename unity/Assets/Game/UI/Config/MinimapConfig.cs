#nullable enable
using System;
using System.Collections.Generic;
using Shared.Data;
using Shared.Data.Tables;
using UnityEngine;

namespace Game.UI.Config
{
    /// <summary>One minimap marker kind as the presenter draws it: a <see cref="MinimapMarkerDef"/> row with its icon.</summary>
    public sealed class MinimapMarkerStyle
    {
        public MinimapMarkerStyle(MinimapMarkerDef row, MinimapIconRegistry? icon)
        {
            Row = row;
            Shape = icon?.PlaceholderShape ?? MinimapShape.Circle;
            IconAddress = icon?.IconAddress ?? string.Empty;
            Color = HudTheme.ColorOf(row.Color);
        }

        public MinimapMarkerDef Row { get; }
        public string Id => Row.Id;
        public MinimapSource Source => Row.Source;
        public MinimapShape Shape { get; }

        /// <summary>The icon texture's Addressables address; empty while the icon is a placeholder shape.</summary>
        public string IconAddress { get; }

        public Color Color { get; }
        public int SizePx => Row.SizePx;
        public int DrawOrder => Row.DrawOrder;
        public int VisibleFromLevel => Row.VisibleFromLevel;
    }

    /// <summary>
    /// The minimap's tables (spec 9.3, 9.12; WP-M2-10b): <see cref="MinimapSettings"/>, the marker kinds of
    /// <see cref="MinimapMarkerDef"/> with their <see cref="MinimapIconRegistry"/> icons, the <see cref="MinimapRegionTint"/>
    /// colors, and the joins the sources need: NPC kinds by <c>NpcDef</c> id, monster ids by net id and the spawn markers of
    /// each monster type (<c>SpawnPoints</c>). Built once when the HUD loads.
    /// </summary>
    public sealed class MinimapConfig
    {
        private readonly Dictionary<string, Color> _tints = new Dictionary<string, Color>(StringComparer.Ordinal);
        private readonly Dictionary<string, NpcKind> _npcKinds = new Dictionary<string, NpcKind>(StringComparer.Ordinal);
        private readonly Dictionary<ushort, string> _monsterIds = new Dictionary<ushort, string>();
        private readonly Dictionary<string, List<string>> _spawnMarkers = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        private readonly List<MinimapMarkerStyle> _markers = new List<MinimapMarkerStyle>();

        private MinimapConfig(MinimapSettings settings)
        {
            Settings = settings;
            WalkableColor = HudTheme.ColorOf(settings.WalkableColor);
            BlockedColor = HudTheme.ColorOf(settings.BlockedColor);
        }

        public MinimapSettings Settings { get; }
        public Color WalkableColor { get; }
        public Color BlockedColor { get; }

        /// <summary>Every marker kind, in draw order then id.</summary>
        public IReadOnlyList<MinimapMarkerStyle> Markers => _markers;

        /// <summary>The ground color of a region's walkable cells (its tint row, else <see cref="WalkableColor"/>).</summary>
        public Color GroundColor(string regionId) => _tints.TryGetValue(regionId, out var color) ? color : WalkableColor;

        public bool TryGetNpcKind(string npcDefId, out NpcKind kind) => _npcKinds.TryGetValue(npcDefId, out kind);

        public bool TryGetMonsterId(ushort netId, out string monsterId) => _monsterIds.TryGetValue(netId, out monsterId!);

        /// <summary>The <c>SpawnPoints</c> marker ids of a monster type, in table order; empty for a type without spawns.</summary>
        public IReadOnlyList<string> SpawnMarkersOf(string monsterId) =>
            _spawnMarkers.TryGetValue(monsterId, out var markers) ? markers : (IReadOnlyList<string>)Array.Empty<string>();

        /// <summary>The first marker kind of a source; null when the table has none.</summary>
        public MinimapMarkerStyle? First(MinimapSource source)
        {
            foreach (var marker in _markers)
            {
                if (marker.Source == source)
                    return marker;
            }
            return null;
        }

        public static MinimapConfig From(ConfigSet set)
        {
            var config = new MinimapConfig(MinimapSettings.Find(set) ?? new MinimapSettings { CmPerPx = 100, CellsPerTexel = 2 });
            var icons = new Dictionary<string, MinimapIconRegistry>(StringComparer.Ordinal);
            foreach (var icon in Rows<MinimapIconRegistry>(set))
                icons[icon.Id] = icon;
            foreach (var row in Rows<MinimapMarkerDef>(set))
                config._markers.Add(new MinimapMarkerStyle(row, icons.TryGetValue(row.IconId, out var icon) ? icon : null));
            config._markers.Sort((a, b) => a.DrawOrder != b.DrawOrder ? a.DrawOrder.CompareTo(b.DrawOrder) : string.CompareOrdinal(a.Id, b.Id));
            foreach (var tint in Rows<MinimapRegionTint>(set))
                config._tints[tint.Id] = HudTheme.ColorOf(tint.Color);
            foreach (var npc in Rows<NpcDef>(set))
                config._npcKinds[npc.Id] = npc.Kind;
            foreach (var monster in Rows<MonsterDef>(set))
                config._monsterIds[(ushort)monster.NetId] = monster.Id;
            foreach (var spawn in Rows<SpawnPoints>(set))
            {
                if (!config._spawnMarkers.TryGetValue(spawn.MonsterId, out var markers))
                    config._spawnMarkers[spawn.MonsterId] = markers = new List<string>();
                markers.Add(spawn.MarkerId);
            }
            return config;
        }

        private static IEnumerable<T> Rows<T>(ConfigSet set)
        {
            if (!set.TryGet(typeof(T), out var table))
                yield break;
            foreach (var row in table.Rows)
                yield return (T)row;
        }
    }
}
