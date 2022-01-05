/**
 *   Copyright (C) 2021 okaygo
 *
 *   https://github.com/misterokaygo/MapAssist/
 *
 *  This program is free software: you can redistribute it and/or modify
 *  it under the terms of the GNU General Public License as published by
 *  the Free Software Foundation, either version 3 of the License, or
 *  (at your option) any later version.
 *
 *  This program is distributed in the hope that it will be useful,
 *  but WITHOUT ANY WARRANTY; without even the implied warranty of
 *  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 *  GNU General Public License for more details.
 *
 *  You should have received a copy of the GNU General Public License
 *  along with this program.  If not, see <https://www.gnu.org/licenses/>.
 **/

using GameOverlay.Drawing;
using GameOverlay.Windows;
using MapAssist.Files.Font;
using MapAssist.Settings;
using MapAssist.Structs;
using MapAssist.Types;
using SharpDX;
using SharpDX.Direct2D1;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Color = System.Drawing.Color;
using Pen = System.Drawing.Pen;

namespace MapAssist.Helpers
{
    public class Compositor : IDisposable
    {
        private static readonly NLog.Logger _log = NLog.LogManager.GetCurrentClassLogger();
        public GameData _gameData;
        public readonly AreaData _areaData;
        private readonly IReadOnlyList<PointOfInterest> _pointsOfInterest;
        ExocetFont _exocetFont;

        private Matrix3x2 mapTransformMatrix;
        private Matrix3x2 areaTransformMatrix;
        private HashSet<(SharpDX.Direct2D1.Bitmap, Point)> gamemaps = new HashSet<(SharpDX.Direct2D1.Bitmap, Point)>();
        private Rectangle _drawBounds;
        private readonly float _rotateRadians = (float)(45 * Math.PI / 180f);
        private float scaleWidth = 1;
        private float scaleHeight = 1;
        private const int WALKABLE = 0;
        private const int BORDER = 1;

        public Compositor(AreaData areaData, IReadOnlyList<PointOfInterest> pointsOfInterest)
        {
            _areaData = areaData;
            _areaData.CalcViewAreas(_rotateRadians);

            foreach (var adjacentArea in _areaData.AdjacentAreas.Values)
            {
                adjacentArea.CalcViewAreas(_rotateRadians);
            }

            _pointsOfInterest = pointsOfInterest;
            _exocetFont = new ExocetFont();
        }

        public void Init(Graphics gfx, GameData gameData, Rectangle drawBounds)
        {
            _gameData = gameData;
            _drawBounds = drawBounds;
            (scaleWidth, scaleHeight) = GetScaleRatios();

            var renderWidth = NavigatorConfiguration.Loaded.RenderingConfiguration.Size * _areaData.ViewOutputRect.Width / _areaData.ViewOutputRect.Height;
            switch (NavigatorConfiguration.Loaded.RenderingConfiguration.Position)
            {
                case MapPosition.TopLeft:
                    _drawBounds.Right = _drawBounds.Left + renderWidth;
                    break;
                case MapPosition.TopRight:
                    _drawBounds.Left = _drawBounds.Right - renderWidth;
                    break;
            }

            CalcTransformMatrices(gfx);

            if (gamemaps.Count > 0) return;

            RenderTarget renderTarget = gfx.GetRenderTarget();

            var areasToRender = (new AreaData[] { _areaData }).Concat(_areaData.AdjacentAreas.Values).ToArray();

            foreach (var renderArea in areasToRender)
            {
                var imageSize = new Size2((int)renderArea.ViewInputRect.Width, (int)renderArea.ViewInputRect.Height);
                var gamemap = new SharpDX.Direct2D1.Bitmap(renderTarget, imageSize, new BitmapProperties(renderTarget.PixelFormat));
                var bytes = new byte[imageSize.Width * imageSize.Height * 4];

                var maybeWalkableColor = NavigatorConfiguration.Loaded.MapColorConfiguration.Walkable;
                var maybeBorderColor = NavigatorConfiguration.Loaded.MapColorConfiguration.Border;

                if (maybeWalkableColor != null || maybeBorderColor != null)
                {
                    var walkableColor = maybeWalkableColor != null ? (Color)maybeWalkableColor : Color.Transparent;
                    var borderColor = maybeBorderColor != null ? (Color)maybeBorderColor : Color.Transparent;

                    for (var y = 0; y < imageSize.Height; y++)
                    {
                        var _y = y + (int)renderArea.ViewInputRect.Top;
                        for (var x = 0; x < imageSize.Width; x++)
                        {
                            var _x = x + (int)renderArea.ViewInputRect.Left;

                            var i = imageSize.Width * 4 * y + x * 4;
                            var type = renderArea.CollisionGrid[_y][_x];

                            // // Uncomment this to show a red border for debugging
                            // if (x == 0 || y == 0 || y == imageSize.Height - 1 || x == imageSize.Width - 1)
                            // {
                            //     bytes[i] = 0;
                            //     bytes[i + 1] = 0;
                            //     bytes[i + 2] = 255;
                            //     bytes[i + 3] = 255;
                            //     continue;
                            // }

                            var pixelColor = type == WALKABLE && maybeWalkableColor != null ? walkableColor :
                                type == BORDER && maybeBorderColor != null ? borderColor :
                                Color.Transparent;

                            if (pixelColor != Color.Transparent)
                            {
                                bytes[i] = pixelColor.B;
                                bytes[i + 1] = pixelColor.G;
                                bytes[i + 2] = pixelColor.R;
                                bytes[i + 3] = pixelColor.A;
                            }
                        }
                    }
                }

                gamemap.CopyFromMemory(bytes, imageSize.Width * 4);
                var origin = renderArea.Origin.Add(renderArea.ViewInputRect.Left - _areaData.ViewInputRect.Left, renderArea.ViewInputRect.Top - _areaData.ViewInputRect.Top);

                gamemaps.Add((gamemap, origin));
            }
        }

        public void DrawGamemap(Graphics gfx)
        {
            if (_gameData.Area != _areaData.Area)
            {
                throw new ApplicationException($"Asked to compose an image for a different area. Compositor area: {_areaData.Area}, Game data: {_areaData.Area}");
            }

            RenderTarget renderTarget = gfx.GetRenderTarget();

            renderTarget.Transform = Matrix3x2.Identity.ToDXMatrix(); // Needed for the draw bounds to work properly
            renderTarget.PushAxisAlignedClip(_drawBounds, AntialiasMode.Aliased);

            renderTarget.Transform = mapTransformMatrix.ToDXMatrix();

            foreach (var (gamemap, origin) in gamemaps)
            {
                DrawBitmap(gfx, gamemap, origin.Subtract(_areaData.Origin).Subtract(_areaData.MapPadding, _areaData.MapPadding), (float)NavigatorConfiguration.Loaded.RenderingConfiguration.Opacity);
            }

            renderTarget.PopAxisAlignedClip();
        }

        public void DrawOverlay(Graphics gfx)
        {
            RenderTarget renderTarget = gfx.GetRenderTarget();

            renderTarget.Transform = Matrix3x2.Identity.ToDXMatrix(); // Needed for the draw bounds to work properly
            renderTarget.PushAxisAlignedClip(_drawBounds, AntialiasMode.Aliased);

            renderTarget.Transform = areaTransformMatrix.ToDXMatrix();

            DrawPointsOfInterest(gfx);
            DrawMonsters(gfx);
            DrawItems(gfx);
            DrawPlayers(gfx);

            renderTarget.PopAxisAlignedClip();
        }

        private void DrawPointsOfInterest(Graphics gfx)
        {
            var drawPoiIcons = new List<(IconRendering, Point)>();
            var drawPoiLabels = new List<(PointOfInterestRendering, Point, string, Color?)>();

            foreach (var poi in _pointsOfInterest)
            {
                if (poi.PoiMatchesPortal(_gameData.Objects, _gameData.Difficulty))
                {
                    continue;
                }

                if (poi.RenderingSettings.CanDrawIcon())
                {
                    drawPoiIcons.Add((poi.RenderingSettings, poi.Position));
                }
            }

            foreach (var poi in _pointsOfInterest)
            {
                if (poi.Area != _areaData.Area && (new PoiType[] { PoiType.PreviousArea, PoiType.NextArea, PoiType.Quest }).Contains(poi.Type))
                {
                    continue;
                }

                if (poi.RenderingSettings.CanDrawLine())
                {
                    var padding = poi.RenderingSettings.CanDrawLabel() ? poi.RenderingSettings.LabelFontSize * 1.3f / 2 : 0; // 1.3f is the line height adjustment
                    var poiPosition = MovePointInBounds(poi.Position, _gameData.PlayerPosition, padding);
                    DrawLine(gfx, poi.RenderingSettings, _gameData.PlayerPosition, poiPosition);
                }

                if (!string.IsNullOrWhiteSpace(poi.Label) && poi.Type != PoiType.Shrine)
                {
                    if (poi.PoiMatchesPortal(_gameData.Objects, _gameData.Difficulty))
                    {
                        continue;
                    }

                    if (poi.RenderingSettings.CanDrawLine() && poi.RenderingSettings.CanDrawLabel())
                    {
                        var poiPosition = MovePointInBounds(poi.Position, _gameData.PlayerPosition);
                        drawPoiLabels.Add((poi.RenderingSettings, poiPosition, poi.Label, null));
                    }
                    else if (poi.RenderingSettings.CanDrawLabel())
                    {
                        drawPoiLabels.Add((poi.RenderingSettings, poi.Position, poi.Label, null));
                    }
                }
            }

            var areasToRender = (new AreaData[] { _areaData }).Concat(_areaData.AdjacentAreas.Values).ToArray();
            foreach (var gameObject in _gameData.Objects)
            {
                var foundInArea = areasToRender.FirstOrDefault(area => area.IncludesPoint(gameObject.Position));
                if (foundInArea != null && foundInArea.Area != _areaData.Area && !AreaExtensions.RequiresStitching(foundInArea.Area)) continue; // Don't show gamedata objects in another area if areas aren't stitched together

                if (gameObject.IsShrine() || gameObject.IsWell())
                {
                    if (NavigatorConfiguration.Loaded.MapConfiguration.Shrine.CanDrawIcon())
                    {
                        drawPoiIcons.Add((NavigatorConfiguration.Loaded.MapConfiguration.Shrine, gameObject.Position));
                    }

                    if (NavigatorConfiguration.Loaded.MapConfiguration.Shrine.CanDrawLabel())
                    {
                        var label = Shrine.ShrineDisplayName(gameObject);
                        drawPoiLabels.Add((NavigatorConfiguration.Loaded.MapConfiguration.Shrine, gameObject.Position, label, null));
                    }

                    continue;
                }
                
                if (gameObject.IsPortal())
                {
                    var destinationArea = (Area)Enum.ToObject(typeof(Area), gameObject.ObjectData.InteractType);

                    if (NavigatorConfiguration.Loaded.MapConfiguration.Portal.CanDrawIcon())
                    {
                        drawPoiIcons.Add((NavigatorConfiguration.Loaded.MapConfiguration.Portal, gameObject.Position));
                    }

                    if (NavigatorConfiguration.Loaded.MapConfiguration.Portal.CanDrawLabel(destinationArea))
                    {
                        var playerName = gameObject.ObjectData.Owner.Length > 0 ? gameObject.ObjectData.Owner : null;
                        var label = Utils.GetPortalName(destinationArea, _gameData.Difficulty, playerName);

                        if (string.IsNullOrWhiteSpace(label) || label == "None") continue;
                        drawPoiLabels.Add((NavigatorConfiguration.Loaded.MapConfiguration.Portal, gameObject.Position, label, null));
                    }

                    continue;
                }

                if (gameObject.IsChest())
                {
                    if ((gameObject.ObjectData.InteractType & ((byte)Chest.InteractFlags.Trap)) != ((byte)Chest.InteractFlags.None))
                    {
                        if (NavigatorConfiguration.Loaded.MapConfiguration.TrappedChest.CanDrawIcon())
                        {
                            drawPoiIcons.Add((NavigatorConfiguration.Loaded.MapConfiguration.TrappedChest, gameObject.Position));
                        }
                    }

                    if ((gameObject.ObjectData.InteractType & ((byte)Chest.InteractFlags.Locked)) != ((byte)Chest.InteractFlags.None))
                    {
                        if (NavigatorConfiguration.Loaded.MapConfiguration.LockedChest.CanDrawIcon())
                        {
                            drawPoiIcons.Add((NavigatorConfiguration.Loaded.MapConfiguration.LockedChest, gameObject.Position));
                        }
                    }
                    else
                    {
                        if (NavigatorConfiguration.Loaded.MapConfiguration.NormalChest.CanDrawIcon())
                        {
                            drawPoiIcons.Add((NavigatorConfiguration.Loaded.MapConfiguration.NormalChest, gameObject.Position));
                        }
                    }
                }
            }

            // Draw POI icons (not listed in poiRenderingOrder)
            foreach ((var rendering, var position) in drawPoiIcons)
            {
                DrawIcon(gfx, rendering, position);
            }

            // Draw POI labels (not listed in poiRenderingOrder)
            foreach ((var rendering, var position, var text, Color? color) in drawPoiLabels)
            {
                DrawText(gfx, rendering, position, text, color);
            }
        }

        private void DrawMonsters(Graphics gfx)
        {
            var drawMonsterIcons = new List<(IconRendering, Types.UnitAny)>();
            var drawMonsterLabels = new List<(PointOfInterestRendering, Point, string, Color?)>();

            RenderTarget renderTarget = gfx.GetRenderTarget();

            foreach (var unitAny in _gameData.Monsters)
            {
                var mobRender = GetMonsterIconRendering(unitAny.MonsterData);

                if (AreaExtensions.IsTown(_areaData.Area))
                {
                    var npc = (Npc)unitAny.TxtFileNo;
                    if (NpcExtensions.IsTownsfolk(npc))
                    {
                        var npcRender = NavigatorConfiguration.Loaded.MapConfiguration.Npc;
                        if (npcRender.CanDrawIcon())
                        {
                            drawMonsterIcons.Add((npcRender, unitAny));
                            if (npcRender.CanDrawLabel())
                            {
                                drawMonsterLabels.Add((npcRender, unitAny.Position, NpcExtensions.Name(npc), npcRender.LabelColor));
                            }
                        }
                    }
                }
                else if (mobRender.CanDrawIcon())
                {
                    drawMonsterIcons.Add((mobRender, unitAny));
                }
            }

            // All Monster icons must be listed here for rendering
            var monsterRenderingOrder = new IconRendering[]
            {
                NavigatorConfiguration.Loaded.MapConfiguration.Npc,
                NavigatorConfiguration.Loaded.MapConfiguration.NormalMonster,
                NavigatorConfiguration.Loaded.MapConfiguration.MinionMonster,
                NavigatorConfiguration.Loaded.MapConfiguration.ChampionMonster,
                NavigatorConfiguration.Loaded.MapConfiguration.UniqueMonster,
                NavigatorConfiguration.Loaded.MapConfiguration.SuperUniqueMonster,
            };

            foreach (var mobRender in monsterRenderingOrder)
            {
                foreach ((var rendering, var unitAny) in drawMonsterIcons)
                {
                    if (mobRender == rendering)
                    {
                        var monsterPosition = unitAny.Position;

                        DrawIcon(gfx, rendering, monsterPosition);

                        // Draw Monster Immunities on top of monster icon
                        var iCount = unitAny.Immunities.Count;
                        if (iCount > 0)
                        {
                            monsterPosition = Vector2.Transform(monsterPosition.ToVector(), areaTransformMatrix).ToPoint();

                            var currentTransform = renderTarget.Transform;
                            renderTarget.Transform = Matrix3x2.Identity.ToDXMatrix();

                            var iconShape = GetIconShape(mobRender).ToRectangle();

                            var ellipseSize = Math.Max(iconShape.Height / 12, 3 / scaleWidth); // Arbirarily set to be a fraction of the the mob icon size. The important point is that it scales with the mob icon consistently.
                            var dx = ellipseSize * scaleWidth * 1.5f; // Amount of space each indicator will take up, including spacing

                            var iX = -dx * (iCount - 1) / 2f; // Moves the first indicator sufficiently left so that the whole group of indicators will be centered.

                            foreach (var immunity in unitAny.Immunities)
                            {
                                var render = new IconRendering()
                                {
                                    IconShape = Shape.Ellipse,
                                    IconColor = ResistColors.ResistColor[immunity],
                                    IconSize = ellipseSize
                                };

                                var iPoint = monsterPosition.Add(new Point(iX, -iconShape.Height - render.IconSize));
                                DrawIcon(gfx, render, iPoint, equalScaling: true);
                                iX += dx;
                            }

                            renderTarget.Transform = currentTransform;
                        }
                    }
                }
            }

            foreach (var mobRender in monsterRenderingOrder)
            {
                foreach ((var rendering, var position, var text, Color? color) in drawMonsterLabels)
                {
                    if (mobRender == rendering)
                    {
                        DrawText(gfx, rendering, position, text, color);
                    }
                }
            }
        }

        private void DrawItems(Graphics gfx)
        {
            var drawItemIcons = new List<(IconRendering, Point)>();
            var drawItemLabels = new List<(PointOfInterestRendering, Point, string, Color?)>();

            if (NavigatorConfiguration.Loaded.ItemLog.Enabled)
            {
                foreach (var item in _gameData.Items)
                {
                    if (item.IsDropped())
                    {
                        if (!LootFilter.Filter(item))
                        {
                            continue;
                        }

                        var itemPosition = item.Position;
                        var render = NavigatorConfiguration.Loaded.MapConfiguration.Item;

                        drawItemIcons.Add((render, itemPosition));
                    }
                }

                foreach (var item in _gameData.Items)
                {
                    if (item.IsDropped())
                    {
                        if (!LootFilter.Filter(item))
                        {
                            continue;
                        }

                        if (item != null && Items.ItemColors.TryGetValue(item.ItemData.ItemQuality, out var color))
                        {
                            var itemBaseName = Items.ItemNameDisplay(item.TxtFileNo);

                            if (itemBaseName.EndsWith(" Rune") || itemBaseName.StartsWith("Key of "))
                            {
                                color = Items.ItemColors[ItemQuality.CRAFT];
                            }

                            drawItemLabels.Add((NavigatorConfiguration.Loaded.MapConfiguration.Item, item.Position, itemBaseName, color));
                        }
                    }
                }
            }

            foreach ((var rendering, var position) in drawItemIcons)
            {
                DrawIcon(gfx, rendering, position);
            }

            foreach ((var rendering, var position, var text, Color? color) in drawItemLabels)
            {
                DrawText(gfx, rendering, position, text, color);
            }
        }

        private void DrawPlayers(Graphics gfx)
        {
            var drawPlayerIcons = new List<(IconRendering, Point)>();
            var drawPlayerLabels = new List<(PointOfInterestRendering, Point, string, Color?)>();

            var areasToRender = new AreaData[] { _areaData };
            if (AreaExtensions.RequiresStitching(_areaData.Area))
            {
                areasToRender = areasToRender.Concat(_areaData.AdjacentAreas.Values.Where(area => AreaExtensions.RequiresStitching(area.Area))).ToArray();
            }

            Dictionary<uint, Types.UnitAny> corpseList;
            foreach (var player in _gameData.Players.Values)
            {
                if (player.IsCorpse && player.UnitId != _gameData.PlayerUnit.UnitId)
                {
                    if (GameMemory.Corpses.TryGetValue(_gameData.ProcessId, out corpseList))
                    {
                        if (!corpseList.TryGetValue(player.UnitId, out var corpse))
                        {
                            var unitAny = player.Clone();
                            GameMemory.Corpses[_gameData.ProcessId].Add(unitAny.UnitId, unitAny);
                        }
                    }
                }
            }

            if (GameMemory.Corpses.TryGetValue(_gameData.ProcessId, out corpseList) && corpseList.Values.Count > 0)
            {
                var rendering = NavigatorConfiguration.Loaded.MapConfiguration.Corpse;
                var canDrawLabel = rendering.CanDrawLabel();
                var canDrawIcon = rendering.CanDrawIcon();
                var canDrawLine = rendering.CanDrawLine();
                var corpses = corpseList.Values.ToArray();
                foreach (var corpse in corpses)
                {
                    if (corpse.Act.ActId != _gameData.PlayerUnit.Act.ActId) continue; // Don't show corpse if not in the same act
                    if (!areasToRender.Any(area => area.Area == corpse.InitialArea)) continue; // Don't show corpse if not in drawn areas

                    if (canDrawIcon)
                    {
                        drawPlayerIcons.Add((rendering, corpse.Position));
                    }
                    if (canDrawLabel)
                    {
                        var poiPosition = MovePointInBounds(corpse.Position, _gameData.PlayerPosition);
                        drawPlayerLabels.Add((rendering, poiPosition, corpse.Name + " (Corpse)", null)); //fix label when language is merged in
                    }
                    if (canDrawLine && corpse.Name == _gameData.PlayerUnit.Name)
                    {
                        var padding = canDrawLabel ? rendering.LabelFontSize * 1.3f / 2 : 0; // 1.3f is the line height adjustment
                        var poiPosition = MovePointInBounds(corpse.Position, _gameData.PlayerPosition, padding);
                        DrawLine(gfx, rendering, _gameData.PlayerPosition, poiPosition);
                    }
                    if (_gameData.PlayerUnit.DistanceTo(corpse.Position) <= 40)
                    {
                        if (!_gameData.Players.TryGetValue(corpse.UnitId, out var player))
                        {
                            GameMemory.Corpses[_gameData.ProcessId].Remove(corpse.UnitId);
                        }
                    }
                }
            }

            if (_gameData.Roster.EntriesByUnitId.TryGetValue(_gameData.PlayerUnit.UnitId, out var myPlayerEntry))
            {
                foreach (var player in _gameData.Roster.List)
                {
                    var myPlayer = player.UnitId == myPlayerEntry.UnitId;
                    var inMyParty = player.PartyID == myPlayerEntry.PartyID;
                    var playerName = player.Name;

                    var canDrawIcon = NavigatorConfiguration.Loaded.MapConfiguration.Player.CanDrawIcon();
                    var canDrawLabel = NavigatorConfiguration.Loaded.MapConfiguration.Player.CanDrawLabel();

                    if (_gameData.Players.TryGetValue(player.UnitId, out var playerUnit))
                    {
                        if (!myPlayer && playerUnit.Act.ActId != _gameData.PlayerUnit.Act.ActId) continue; // Don't show player if not in the same act
                        if (!myPlayer && !areasToRender.Any(area => area.IncludesPoint(playerUnit.Position))) continue; // Don't show player if not in drawn areas

                        // use data from the unit table if available
                        if (playerUnit.InPlayerParty) // partyid is max if player is not in a party
                        {
                            var rendering = myPlayer
                                ? NavigatorConfiguration.Loaded.MapConfiguration.Player
                                : NavigatorConfiguration.Loaded.MapConfiguration.PartyPlayer;

                            var canDrawThisIcon = myPlayer
                                ? canDrawIcon
                                : NavigatorConfiguration.Loaded.MapConfiguration.PartyPlayer.CanDrawIcon();

                            if (canDrawThisIcon)
                            {
                                drawPlayerIcons.Add((rendering, playerUnit.Position));
                            }

                            var canDrawThisLabel = myPlayer
                                ? canDrawLabel
                                : NavigatorConfiguration.Loaded.MapConfiguration.PartyPlayer.CanDrawLabel();

                            if (canDrawThisLabel && !myPlayer)
                            {
                                drawPlayerLabels.Add((rendering, playerUnit.Position, playerName, rendering.LabelColor));
                            }
                        }
                        else
                        {
                            // not in my party
                            var rendering = (myPlayer
                                ? NavigatorConfiguration.Loaded.MapConfiguration.Player
                                : (playerUnit.HostileToPlayer
                                    ? NavigatorConfiguration.Loaded.MapConfiguration.HostilePlayer
                                    : NavigatorConfiguration.Loaded.MapConfiguration.NonPartyPlayer));

                            if (rendering.CanDrawIcon())
                            {
                                drawPlayerIcons.Add((rendering, playerUnit.Position));
                            }

                            if (rendering.CanDrawLine() && playerUnit.HostileToPlayer)
                            {
                                var padding = rendering.CanDrawLabel() ? NavigatorConfiguration.Loaded.MapConfiguration.HostilePlayer.LabelFontSize * 1.3f / 2 : 0; // 1.3f is the line height adjustment
                                var poiPosition = MovePointInBounds(playerUnit.Position, _gameData.PlayerPosition, padding);
                                DrawLine(gfx, NavigatorConfiguration.Loaded.MapConfiguration.HostilePlayer, _gameData.PlayerPosition, poiPosition);
                            }

                            if (rendering.CanDrawLabel() && !myPlayer)
                            {
                                drawPlayerLabels.Add((rendering, playerUnit.Position, playerName, rendering.LabelColor));
                            }
                        }
                    }
                    else
                    {
                        if (!myPlayer && !areasToRender.Select(area => area.Area).Contains(player.Area)) continue; // Don't show player if not in drawn areas

                        // otherwise use the data from the roster
                        // only draw if in the same party, otherwise position/area data will not be up to date
                        if (inMyParty && player.PartyID < ushort.MaxValue)
                        {
                            var rendering = NavigatorConfiguration.Loaded.MapConfiguration.PartyPlayer;
                            if (canDrawIcon)
                            {
                                drawPlayerIcons.Add((rendering, player.Position));
                            }

                            if (canDrawLabel && !myPlayer)
                            {
                                drawPlayerLabels.Add((rendering, player.Position, playerName, rendering.LabelColor));
                            }
                        }
                    }
                }
            }

            // All Player icons must be listed here for rendering
            var playersRenderingOrder = new IconRendering[]
            {
                NavigatorConfiguration.Loaded.MapConfiguration.Corpse,
                NavigatorConfiguration.Loaded.MapConfiguration.NonPartyPlayer,
                NavigatorConfiguration.Loaded.MapConfiguration.PartyPlayer,
                NavigatorConfiguration.Loaded.MapConfiguration.Player,
                NavigatorConfiguration.Loaded.MapConfiguration.HostilePlayer,
            };

            foreach (var renderOrder in playersRenderingOrder)
            {
                foreach ((var rendering, var position) in drawPlayerIcons)
                {
                    if (renderOrder == rendering)
                    {
                        DrawIcon(gfx, rendering, position);
                    }
                }
            }

            foreach (var renderOrder in playersRenderingOrder)
            {
                foreach ((var rendering, var position, var text, Color? color) in drawPlayerLabels)
                {
                    if (renderOrder == rendering)
                    {
                        DrawText(gfx, rendering, position, text, color);
                    }
                }
            }
        }

        public void DrawBuffs(Graphics gfx)
        {
            RenderTarget renderTarget = gfx.GetRenderTarget();
            renderTarget.Transform = Matrix3x2.Identity.ToDXMatrix();

            var buffImageScale = (float)NavigatorConfiguration.Loaded.RenderingConfiguration.BuffSize;
            if (buffImageScale <= 0)
            {
                return;
            }

            var stateList = _gameData.PlayerUnit.StateList;
            var imgDimensions = 48f * buffImageScale;

            var buffAlignment = NavigatorConfiguration.Loaded.RenderingConfiguration.BuffPosition;
            var buffYPos = 0f;

            switch (buffAlignment)
            {
                case BuffPosition.Player:
                    buffYPos = (gfx.Height / 2f) - imgDimensions - (gfx.Height * .12f);
                    break;
                case BuffPosition.Top:
                    buffYPos = gfx.Height * .12f;
                    break;
                case BuffPosition.Bottom:
                    buffYPos = gfx.Height * .8f;
                    break;

            }

            var buffsByColor = new Dictionary<Color, List<SharpDX.Direct2D1.Bitmap>>();
            var totalBuffs = 0;

            buffsByColor.Add(States.DebuffColor, new List<SharpDX.Direct2D1.Bitmap>());
            buffsByColor.Add(States.PassiveColor, new List<SharpDX.Direct2D1.Bitmap>());
            buffsByColor.Add(States.AuraColor, new List<SharpDX.Direct2D1.Bitmap>());
            buffsByColor.Add(States.BuffColor, new List<SharpDX.Direct2D1.Bitmap>());

            foreach (var state in stateList)
            {
                var stateStr = Enum.GetName(typeof(State), state).Substring(6);
                var resImg = Properties.Resources.ResourceManager.GetObject(stateStr);

                if (resImg != null)
                {
                    Color buffColor = States.StateColor(state);
                    if (state == State.STATE_CONVICTION)
                    {
                        if (_gameData.PlayerUnit.Skill.RightSkillId == Skills.SKILL_CONVICTION) //add check later for if infinity is equipped
                        {
                            buffColor = States.BuffColor;
                        }
                        else
                        {
                            buffColor = States.DebuffColor;
                        }
                    }
                    if (buffsByColor.TryGetValue(buffColor, out var _))
                    {
                        buffsByColor[buffColor].Add(CreateResourceBitmap(gfx, stateStr));
                        totalBuffs++;
                    }
                }
            }

            var buffIndex = 1;
            foreach (var buff in buffsByColor)
            {
                for (var i = 0; i < buff.Value.Count; i++)
                {
                    var buffImg = buff.Value[i];
                    var buffColor = buff.Key;
                    var drawPoint = new Point((gfx.Width / 2f) - (buffIndex * imgDimensions) - (buffIndex * buffImageScale) - (totalBuffs * buffImageScale / 2f) + (totalBuffs * imgDimensions / 2f) + (totalBuffs * buffImageScale), buffYPos);
                    DrawBitmap(gfx, buffImg, drawPoint, 1, size: buffImageScale);

                    var pen = new Pen(buffColor, buffImageScale);
                    if (buffColor == States.DebuffColor)
                    {
                        var size = new Point(imgDimensions - buffImageScale + buffImageScale + buffImageScale, imgDimensions - buffImageScale + buffImageScale + buffImageScale);
                        var rect = new Rectangle(drawPoint.X, drawPoint.Y, drawPoint.X + size.X, drawPoint.Y + size.Y);

                        var debuffColor = States.DebuffColor;
                        debuffColor = Color.FromArgb(100, debuffColor.R, debuffColor.G, debuffColor.B);
                        var brush = CreateSolidBrush(gfx, debuffColor, 1);

                        gfx.FillRectangle(brush, rect);
                        gfx.DrawRectangle(brush, rect, 1);
                    }
                    else
                    {
                        var size = new Point(imgDimensions - buffImageScale + buffImageScale, imgDimensions - buffImageScale + buffImageScale);
                        var rect = new Rectangle(drawPoint.X, drawPoint.Y, drawPoint.X + size.X, drawPoint.Y + size.Y);

                        var brush = CreateSolidBrush(gfx, buffColor, 1);
                        gfx.DrawRectangle(brush, rect, 1);
                    }

                    buffIndex++;
                }
            }
        }

        public void DrawGameInfo(Graphics gfx, Point anchor,
            DrawGraphicsEventArgs e, bool errorLoadingAreaData)
        {
            if (_gameData.MenuPanelOpen >= 2)
            {
                return;
            }

            RenderTarget renderTarget = gfx.GetRenderTarget();
            renderTarget.Transform = Matrix3x2.Identity.ToDXMatrix();

            // Setup
            var font = NavigatorConfiguration.Loaded.GameInfo.LabelFont;
            var fontSize = NavigatorConfiguration.Loaded.GameInfo.LabelFontSize;
            var fontHeight = (fontSize + fontSize / 2f);

            // Game IP
            if (NavigatorConfiguration.Loaded.GameInfo.ShowGameIP)
            {
                var fontColor = _gameData.Session.GameIP == NavigatorConfiguration.Loaded.GameInfo.HuntingIP ? Color.Green : Color.Red;

                var ipText = "Game IP: " + _gameData.Session.GameIP;
                DrawText(gfx, anchor, ipText, font, fontSize, fontColor);

                anchor.Y += fontHeight + 5;
            }

            // Area Level
            if (NavigatorConfiguration.Loaded.GameInfo.ShowAreaLevel)
            {
                // Area Label
                var areaText = "Area: " + Utils.GetAreaLabel(_areaData.Area, _gameData.Difficulty, true);
                DrawText(gfx, anchor, areaText, font, fontSize, Color.FromArgb(255, 218, 100));

                anchor.Y += fontHeight + 5;
            }

            // Overlay FPS
            if (NavigatorConfiguration.Loaded.GameInfo.ShowOverlayFPS)
            {
                var fpsText = "FPS: " + gfx.FPS.ToString() + "   " + "DeltaTime: " + e.DeltaTime.ToString();
                DrawText(gfx, anchor, fpsText, font, fontSize, Color.FromArgb(0, 255, 0));

                anchor.Y += fontHeight + 5;
            }

            if (errorLoadingAreaData)
            {
                DrawText(gfx, anchor, "ERROR LOADING GAME MAP!", font, (int)Math.Round(fontSize * 1.5), Color.Orange);
                anchor.Y += (int)Math.Round(fontHeight * 1.5) + 5;
            }

            DrawItemLog(gfx, anchor);
        }

        public void DrawItemLog(Graphics gfx, Point anchor)
        {
            if (_gameData.MenuPanelOpen >= 2)
            {
                return;
            }

            // Setup
            var fontSize = (float)NavigatorConfiguration.Loaded.ItemLog.LabelFontSize;
            var fontHeight = (fontSize + fontSize / 2f);

            // Item Log
            var ItemLog = Items.CurrentItemLog.ToArray();
            for (var i = 0; i < ItemLog.Length; i++)
            {
                var item = ItemLog[i];

                Color fontColor;
                if (item == null || !Items.ItemColors.TryGetValue(item.ItemData.ItemQuality, out fontColor))
                {
                    // Invalid item quality
                    continue;
                }

                var font = CreateFont(gfx, NavigatorConfiguration.Loaded.ItemLog.LabelFont, (float)NavigatorConfiguration.Loaded.ItemLog.LabelFontSize);

                var isEth = (item.ItemData.ItemFlags & ItemFlags.IFLAG_ETHEREAL) == ItemFlags.IFLAG_ETHEREAL;
                var itemBaseName = Items.ItemNameDisplay(item.TxtFileNo);
                var itemSpecialName = "";
                var itemLabelExtra = "";

                if (isEth)
                {
                    itemLabelExtra += "[Eth] ";
                    if (fontColor == Color.White)
                    {
                        fontColor = Items.ItemColors[ItemQuality.SUPERIOR];
                    }
                }

                if (item.Stats.TryGetValue(Stat.STAT_ITEM_NUMSOCKETS, out var numSockets))
                {
                    itemLabelExtra += "[" + numSockets + " S] ";
                    if (fontColor == Color.White)
                    {
                        fontColor = Items.ItemColors[ItemQuality.SUPERIOR];
                    }
                }

                if (itemBaseName.EndsWith(" Rune") || itemBaseName.StartsWith("Key of "))
                {
                    fontColor = Items.ItemColors[ItemQuality.CRAFT];
                }

                switch (item.ItemData.ItemQuality)
                {
                    case ItemQuality.UNIQUE:
                        itemSpecialName = Items.UniqueName(item.TxtFileNo) + " ";
                        break;
                    case ItemQuality.SET:
                        itemSpecialName = Items.SetName(item.TxtFileNo) + " ";
                        break;
                }

                var brush = CreateSolidBrush(gfx, fontColor, 1);

                gfx.DrawText(font, brush, anchor.Add(0, i * fontHeight), itemLabelExtra + itemSpecialName + itemBaseName);
            }
        }

        // Drawing Utility Functions
        private void DrawBitmap(Graphics gfx, SharpDX.Direct2D1.Bitmap bitmapDX, Point anchor, float opacity,
            float size = 1)
        {
            RenderTarget renderTarget = gfx.GetRenderTarget();

            var sourceRect = new Rectangle(0, 0, bitmapDX.Size.Width, bitmapDX.Size.Height);
            var destRect = new Rectangle(
                anchor.X,
                anchor.Y,
                anchor.X + bitmapDX.Size.Width * size,
                anchor.Y + bitmapDX.Size.Height * size);

            renderTarget.DrawBitmap(bitmapDX, destRect, opacity, BitmapInterpolationMode.Linear, sourceRect);
        }

        private void DrawIcon(Graphics gfx, IconRendering rendering, Point position,
            bool equalScaling = false)
        {
            var renderTarget = gfx.GetRenderTarget();
            var currentTransform = renderTarget.Transform;
            renderTarget.Transform = Matrix3x2.Identity.ToDXMatrix();

            position = Vector2.Transform(position.ToVector(), currentTransform.ToMatrix()).ToPoint();

            var fill = !rendering.IconShape.ToString().ToLower().EndsWith("outline");
            var fillBrush = CreateSolidBrush(gfx, rendering.IconColor);
            var outlineBrush = CreateSolidBrush(gfx, rendering.IconOutlineColor);

            var points = GetIconShape(rendering, equalScaling).Select(point => point.Add(position)).ToArray();

            var _scaleHeight = equalScaling ? scaleWidth : scaleHeight;

            using (var geo = points.ToGeometry(gfx, fill))
            {
                switch (rendering.IconShape)
                {
                    case Shape.Ellipse:
                        if (rendering.IconColor.A > 0)
                        {
                            gfx.FillEllipse(fillBrush, position, rendering.IconSize * scaleWidth / 2, rendering.IconSize * _scaleHeight / 2); // Divide by 2 because the parameter requires a radius
                        }
                        
                        if (rendering.IconOutlineColor.A > 0)
                        {
                            gfx.DrawEllipse(outlineBrush, position, rendering.IconSize * scaleWidth / 2, rendering.IconSize * _scaleHeight / 2, rendering.IconThickness); // Divide by 2 because the parameter requires a radius
                        }

                        break;
                    case Shape.Portal:
                        if (rendering.IconColor.A > 0)
                        {
                            gfx.FillEllipse(fillBrush, position, rendering.IconSize * scaleWidth / 2, rendering.IconSize * 2 * scaleWidth / 2); // Use scaleWidth so it doesn't shrink the height in overlay mode, allows portal to look the same in both modes
                        }

                        if (rendering.IconOutlineColor.A > 0)
                        {
                            gfx.DrawEllipse(outlineBrush, position, rendering.IconSize * scaleWidth / 2, rendering.IconSize * 2 * scaleWidth / 2, rendering.IconThickness); // Use scaleWidth so it doesn't shrink the height in overlay mode, allows portal to look the same in both modes
                        }

                        break;
                    default:
                        if (points == null) break;

                        if (rendering.IconColor.A > 0)
                        {
                            gfx.FillGeometry(geo, fillBrush);
                        }

                        if (rendering.IconOutlineColor.A > 0)
                        {
                            gfx.DrawGeometry(geo, outlineBrush, rendering.IconThickness);
                        }

                        break;
                }
            }

            renderTarget.Transform = currentTransform;
        }

        private void DrawLine(Graphics gfx, PointOfInterestRendering rendering, Point startPosition, Point endPosition)
        {
            var renderTarget = gfx.GetRenderTarget();
            var currentTransform = renderTarget.Transform;
            renderTarget.Transform = Matrix3x2.Identity.ToDXMatrix();

            startPosition = Vector2.Transform(startPosition.ToVector(), areaTransformMatrix).ToPoint();
            endPosition = Vector2.Transform(endPosition.ToVector(), areaTransformMatrix).ToPoint();

            var angle = endPosition.Subtract(startPosition).Angle();
            var length = endPosition.Rotate(-angle, startPosition).X - startPosition.X;

            var brush = CreateSolidBrush(gfx, rendering.LineColor);

            startPosition = startPosition.Rotate(-angle, startPosition).Add(5 * scaleWidth, 0).Rotate(angle, startPosition); // Add 5 for a little extra spacing from the start point

            if (length > 60) // Don't render when line is too short
            {
                if (rendering.CanDrawArrowHead())
                {
                    endPosition = endPosition.Rotate(-angle, startPosition).Subtract(5 * scaleWidth, 0).Rotate(angle, startPosition); // Subtract 5 for a little extra spacing from the end point

                    var points = new Point[]
                    {
                        new Point((float)(Math.Sqrt(3) / -2), 0.5f),
                        new Point((float)(Math.Sqrt(3) / -2), -0.5f),
                        new Point(0, 0),
                    }.Select(point => point.Multiply(rendering.ArrowHeadSize).Rotate(angle).Add(endPosition)).ToArray(); // Divide by 2 to make the line end inside the triangle

                    endPosition = endPosition.Rotate(-angle, startPosition).Subtract(rendering.ArrowHeadSize / 2f, 0).Rotate(angle, startPosition); // Make the line end inside the triangle

                    gfx.DrawLine(brush, startPosition, endPosition, rendering.LineThickness);
                    gfx.FillTriangle(brush, points[0], points[1], points[2]);
                }
                else
                {
                    gfx.DrawLine(brush, startPosition, endPosition, rendering.LineThickness);
                }
            }

            renderTarget.Transform = currentTransform;
        }

        private void DrawText(Graphics gfx, PointOfInterestRendering rendering, Point position, string text,
            Color? color = null)
        {
            var renderTarget = gfx.GetRenderTarget();
            var currentTransform = renderTarget.Transform;
            renderTarget.Transform = Matrix3x2.Identity.ToDXMatrix();

            var playerCoord = Vector2.Transform(_gameData.PlayerPosition.ToVector(), areaTransformMatrix);
            position = Vector2.Transform(position.ToVector(), areaTransformMatrix).ToPoint();

            var useColor = color ?? rendering.LabelColor;

            var font = CreateFont(gfx, rendering.LabelFont, rendering.LabelFontSize);
            var iconShape = GetIconShape(rendering).ToRectangle();
            var textSize = gfx.MeasureString(font, text);

            var multiplier = playerCoord.Y < position.Y ? 1 : -1;
            if (rendering.CanDrawIcon())
            {
                position = position.Add(new Point(0, iconShape.Height / 2 * (!rendering.CanDrawArrowHead() ? -1 : multiplier)));
            }

            position = position.Add(new Point(0, (textSize.Y / 2 + 5) * (!rendering.CanDrawArrowHead() ? -1 : multiplier)));
            position = MoveTextInBounds(position, text, textSize);

            DrawText(gfx, position, text, rendering.LabelFont, rendering.LabelFontSize, useColor,
                centerText: true);

            renderTarget.Transform = currentTransform;
        }

        private void DrawText(Graphics gfx, Point position, string text, string fontFamily, float fontSize, Color color,
            bool centerText = false)
        {
            var font = CreateFont(gfx, fontFamily, fontSize);
            var brush = CreateSolidBrush(gfx, color, 1);

            if (centerText)
            {
                var stringSize = gfx.MeasureString(font, text);
                position = position.Subtract(stringSize.X / 2, stringSize.Y / 2);
            }

            gfx.DrawText(font, brush, position, text);
        }

        // Utility Functions
        private Point[] GetIconShape(IconRendering render,
            bool equalScaling = false)
        {
            var _scaleHeight = equalScaling ? scaleWidth : scaleHeight;

            switch (render.IconShape)
            {
                case Shape.Square:
                    return new Point[]
                    {
                        new Point(0, 0),
                        new Point(render.IconSize, 0),
                        new Point(render.IconSize, render.IconSize),
                        new Point(0, render.IconSize)
                    }.Select(point => point.Subtract(render.IconSize / 2f).Rotate(_rotateRadians).Multiply(scaleWidth, _scaleHeight)).ToArray();
                case Shape.Ellipse: // Use a rectangle since that's effectively the same size and that's all this function is used for at the moment
                    return new Point[]
                    {
                        new Point(0, 0),
                        new Point(render.IconSize, 0),
                        new Point(render.IconSize, render.IconSize),
                        new Point(0, render.IconSize)
                    }.Select(point => point.Subtract(render.IconSize / 2f).Rotate(_rotateRadians).Multiply(scaleWidth, _scaleHeight)).ToArray();
                case Shape.Portal: // Use a rectangle since that's effectively the same size and that's all this function is used for at the moment
                    return new Point[]
                    {
                        new Point(0, 0),
                        new Point(render.IconSize, 0),
                        new Point(render.IconSize, render.IconSize),
                        new Point(0, render.IconSize)
                    }.Select(point => point.Subtract(render.IconSize / 2f).Rotate(_rotateRadians).Multiply(scaleWidth, scaleWidth * 2)).ToArray(); // Use scaleWidth so it doesn't shrink the height in overlay mode, allows portal to look the same in both modes
                case Shape.Polygon:
                    var halfSize = render.IconSize / 2f;
                    var cutSize = render.IconSize / 10f;

                    return new Point[]
                    {
                        new Point(0, halfSize), new Point(halfSize - cutSize, halfSize - cutSize),
                        new Point(halfSize, 0), new Point(halfSize + cutSize, halfSize - cutSize),
                        new Point(render.IconSize, halfSize),
                        new Point(halfSize + cutSize, halfSize + cutSize),
                        new Point(halfSize, render.IconSize),
                        new Point(halfSize - cutSize, halfSize + cutSize)
                    }.Select(point => point.Subtract(halfSize).Multiply(scaleWidth, _scaleHeight)).ToArray();
                case Shape.Cross:
                    var a = render.IconSize * 0.25f;
                    var b = render.IconSize * 0.50f;
                    var c = render.IconSize * 0.75f;
                    var d = render.IconSize;

                    return new Point[]
                    {
                        new Point(0, a), new Point(a, 0), new Point(b, a), new Point(c, 0),
                        new Point(d, a), new Point(c, b), new Point(d, c), new Point(c, d),
                        new Point(b, c), new Point(a, d), new Point(0, c), new Point(a, b)
                    }.Select(point => point.Subtract(render.IconSize / 2f).Multiply(scaleWidth, _scaleHeight)).ToArray();
            }

            return new Point[]
            {
                new Point(0, 0)
            };
        }

        private IconRendering GetMonsterIconRendering(MonsterData monsterData)
        {
            if ((monsterData.MonsterType & MonsterTypeFlags.Champion) == MonsterTypeFlags.Champion)
            {
                return NavigatorConfiguration.Loaded.MapConfiguration.ChampionMonster;
            }

            if ((monsterData.MonsterType & MonsterTypeFlags.SuperUnique) == MonsterTypeFlags.SuperUnique)
            {
                return NavigatorConfiguration.Loaded.MapConfiguration.SuperUniqueMonster;
            }

            if ((monsterData.MonsterType & MonsterTypeFlags.Minion) == MonsterTypeFlags.Minion)
            {
                return NavigatorConfiguration.Loaded.MapConfiguration.MinionMonster;
            }

            if ((monsterData.MonsterType & MonsterTypeFlags.Unique) == MonsterTypeFlags.Unique)
            {
                return NavigatorConfiguration.Loaded.MapConfiguration.UniqueMonster;
            }

            return NavigatorConfiguration.Loaded.MapConfiguration.NormalMonster;
        }

        private Point MovePointInBounds(Point point, Point origin,
            float padding = 0)
        {
            var resizeScale = 1f;

            var bounds = new Rectangle(_drawBounds.Left + padding, _drawBounds.Top + padding, _drawBounds.Right - padding, _drawBounds.Bottom - padding);
            var startScreenCoord = Vector2.Transform(origin.ToVector(), areaTransformMatrix);
            var endScreenCoord = Vector2.Transform(point.ToVector(), areaTransformMatrix);

            if (endScreenCoord.X < bounds.Left) resizeScale = Math.Min(resizeScale, (bounds.Left - startScreenCoord.X) / (endScreenCoord.X - startScreenCoord.X));
            if (endScreenCoord.X > bounds.Right) resizeScale = Math.Min(resizeScale, (bounds.Right - startScreenCoord.X) / (endScreenCoord.X - startScreenCoord.X));
            if (endScreenCoord.Y < bounds.Top) resizeScale = Math.Min(resizeScale, (bounds.Top - startScreenCoord.Y) / (endScreenCoord.Y - startScreenCoord.Y));
            if (endScreenCoord.Y > bounds.Bottom) resizeScale = Math.Min(resizeScale, (bounds.Bottom - startScreenCoord.Y) / (endScreenCoord.Y - startScreenCoord.Y));

            if (resizeScale < 1)
            {
                return point.Subtract(origin).Multiply(resizeScale).Add(origin);
            }
            else
            {
                return point;
            }
        }

        private Point MoveTextInBounds(Point point, string text, Point size)
        {
            var halfSize = size.Multiply(1 / 2f);

            if (point.X - halfSize.X < _drawBounds.Left) point.X += _drawBounds.Left - point.X + halfSize.X + 1; // Single extra pixel to prevent GameOverlay from word wrapping
            if (point.X + halfSize.X > _drawBounds.Right) point.X += _drawBounds.Right - point.X - halfSize.X - 1; // Single extra pixel to prevent GameOverlay from word wrapping
            if (point.Y - halfSize.Y < _drawBounds.Top) point.Y += _drawBounds.Top - point.Y + halfSize.Y + 1; // Single extra pixel to prevent GameOverlay from word wrapping
            if (point.Y + halfSize.Y > _drawBounds.Bottom) point.Y += _drawBounds.Bottom - point.Y - halfSize.Y - 1; // Single extra pixel to prevent GameOverlay from word wrapping

            return point;
        }

        private (float, float) GetScaleRatios()
        {
            var multiplier = 5.5f - (float)NavigatorConfiguration.Loaded.RenderingConfiguration.ZoomLevel; // Hitting +/- should make the map bigger/smaller, respectively, like in overlay = false mode

            if (!NavigatorConfiguration.Loaded.RenderingConfiguration.OverlayMode)
            {
                multiplier = NavigatorConfiguration.Loaded.RenderingConfiguration.Size / _areaData.ViewOutputRect.Height;

                if (multiplier == 0)
                {
                    multiplier = 1;
                }
            }
            else if (NavigatorConfiguration.Loaded.RenderingConfiguration.Position != MapPosition.Center)
            {
                multiplier *= 0.5f;
            }

            if (multiplier != 1 || NavigatorConfiguration.Loaded.RenderingConfiguration.OverlayMode)
            {
                var heightShrink = NavigatorConfiguration.Loaded.RenderingConfiguration.OverlayMode ? 0.5f : 1f;
                var widthShrink = NavigatorConfiguration.Loaded.RenderingConfiguration.OverlayMode ? 1f : 1f;

                return (multiplier * widthShrink, multiplier * heightShrink);
            }
            else
            {
                return (multiplier, multiplier);
            }
        }

        private void CalcTransformMatrices(Graphics gfx)
        {
            mapTransformMatrix = Matrix3x2.Identity;

            if (NavigatorConfiguration.Loaded.RenderingConfiguration.OverlayMode)
            {
                mapTransformMatrix = Matrix3x2.CreateTranslation(_areaData.Origin.ToVector())
                    * Matrix3x2.CreateTranslation(Vector2.Negate(_gameData.PlayerPosition.ToVector()))
                    * Matrix3x2.CreateRotation(_rotateRadians)
                    * Matrix3x2.CreateScale(scaleWidth, scaleHeight);

                if (NavigatorConfiguration.Loaded.RenderingConfiguration.Position == MapPosition.Center)
                {
                    mapTransformMatrix *= Matrix3x2.CreateTranslation(new Vector2(gfx.Width / 2, gfx.Height / 2))
                        * Matrix3x2.CreateTranslation(new Vector2(2, -8)); // Brute forced to perfectly line up with the in game map;
                }
                else
                {
                    mapTransformMatrix *= Matrix3x2.CreateTranslation(new Vector2(_drawBounds.Left, _drawBounds.Top))
                        * Matrix3x2.CreateTranslation(new Vector2(_drawBounds.Width / 2, _drawBounds.Height / 2));
                }
            }
            else
            {
                mapTransformMatrix = Matrix3x2.CreateTranslation(new Vector2(_areaData.MapPadding / 2, _areaData.MapPadding / 2))
                    * Matrix3x2.CreateTranslation(Vector2.Negate(new Vector2(_areaData.ViewInputRect.Width / 2, _areaData.ViewInputRect.Height / 2)))
                    * Matrix3x2.CreateRotation(_rotateRadians)
                    * Matrix3x2.CreateTranslation(Vector2.Negate(new Vector2(_areaData.ViewOutputRect.Left, _areaData.ViewOutputRect.Top)))
                    * Matrix3x2.CreateScale(scaleWidth, scaleHeight)
                    * Matrix3x2.CreateTranslation(new Vector2(_drawBounds.Left, _drawBounds.Top));

                if (NavigatorConfiguration.Loaded.RenderingConfiguration.Position == MapPosition.Center)
                {
                    mapTransformMatrix *= Matrix3x2.CreateTranslation(new Vector2(_drawBounds.Width / 2, _drawBounds.Height / 2))
                        * Matrix3x2.CreateTranslation(Vector2.Negate(new Vector2(_areaData.ViewOutputRect.Width / 2 * scaleWidth, _areaData.ViewOutputRect.Height / 2 * scaleHeight)));
                }
            }

            areaTransformMatrix = Matrix3x2.CreateTranslation(Vector2.Negate(_areaData.Origin.ToVector()));

            if (!NavigatorConfiguration.Loaded.RenderingConfiguration.OverlayMode)
            {
                areaTransformMatrix *= Matrix3x2.CreateTranslation(Vector2.Negate(new Vector2(_areaData.ViewInputRect.Left, _areaData.ViewInputRect.Top)));
            }

            areaTransformMatrix *= mapTransformMatrix;
        }

        // Creates and cached resources
        private Dictionary<string, SharpDX.Direct2D1.Bitmap> cacheBitmaps = new Dictionary<string, SharpDX.Direct2D1.Bitmap>();
        private SharpDX.Direct2D1.Bitmap CreateResourceBitmap(Graphics gfx, string name)
        {
            var key = name;

            if (!cacheBitmaps.ContainsKey(key))
            {
                var renderTarget = gfx.GetRenderTarget();

                var resImg = Properties.Resources.ResourceManager.GetObject(name);
                cacheBitmaps[key] = new System.Drawing.Bitmap((System.Drawing.Bitmap)resImg).ToDXBitmap(renderTarget);
            }

            return cacheBitmaps[key];
        }

        private Dictionary<(string, float), Font> cacheFonts = new Dictionary<(string, float), Font>();
        private Font CreateFont(Graphics gfx, string fontFamilyName, float size)
        {
            var key = (fontFamilyName, size);
            if (!cacheFonts.ContainsKey(key))
            {
                if (fontFamilyName.Equals("Exocet Blizzard Mixed Caps"))
                {
                    cacheFonts[key] = _exocetFont.CreateFont(size);
                }
                else
                {
                    cacheFonts[key] = gfx.CreateFont(fontFamilyName, size);
                }
            }

            return cacheFonts[key];
        }

        private Dictionary<(Color, float?), SolidBrush> cacheBrushes = new Dictionary<(Color, float?), SolidBrush>();
        private SolidBrush CreateSolidBrush(Graphics gfx, Color color,
            float? opacity = null)
        {
            if (opacity == null) opacity = (float)NavigatorConfiguration.Loaded.RenderingConfiguration.IconOpacity;

            var key = (color, opacity);
            if (!cacheBrushes.ContainsKey(key)) cacheBrushes[key] = gfx.CreateSolidBrush(color.SetOpacity((float)opacity).ToGameOverlayColor());
            return cacheBrushes[key];
        }

        ~Compositor() => Dispose();

        public void Dispose()
        {
            foreach (var (gamemap, _) in gamemaps)
            {
                if (gamemap != null) gamemap.Dispose();
            }

            gamemaps = new HashSet<(SharpDX.Direct2D1.Bitmap, Point)>();

            foreach (var item in cacheBitmaps.Values) item.Dispose();
            foreach (var item in cacheFonts.Values) item.Dispose();
            foreach (var item in cacheBrushes.Values) item.Dispose();
        }
    }
}
