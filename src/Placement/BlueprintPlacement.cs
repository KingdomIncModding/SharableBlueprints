using System;
using System.Linq;
using System.Collections.Generic;
using System.IO;
using KingdomFoundation.Api;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SharableBlueprints
{
    public sealed class BlueprintPlacement : MonoBehaviour
    {
        public static void Begin(SharableBlueprintsPlugin owner, PortableBlueprint definition, string entryId)
        {
            var source = FoundationApi.Blueprints.DeserializeWorkspace(definition.Document["layout"]);
            if (source.Buildings.Count == 0 || source.Pipes.Count != definition.Pipes || source.Pipes.Contains(null))
                throw new InvalidDataException("This blueprint has connections that the installed game cannot load.");
            var manager = FoundationApi.WorkspaceAccess.Root;
            FoundationApi.UiAccess.CancelDragDrop();
            var root = FoundationApi.WorkspaceAccess.ShowPurchaseBlueprint(source.Buildings[0], false);
            try
            {
                var tag = root.gameObject.AddComponent<BlueprintPlacement>();
                tag.owner = owner;
                tag.definition = definition;
                tag.source = source;
                tag.player = FoundationApi.Kingdom;
                tag.destination = FoundationApi.Kingdom.ActiveWorkspace;
                tag.entryId = entryId;
                var rootRect = (RectTransform)root.transform;
                Vector2Int origin = source.Buildings[0].Position - new Vector2Int(0, Mathf.RoundToInt(rootRect.sizeDelta.y));
                for (int i = 1; i < source.Buildings.Count; i++)
                {
                    var child = FoundationApi.Blueprints.CreateChild(root);
                    FoundationApi.Blueprints.SetBuilding(child, source.Buildings[i], false);
                    FoundationApi.Blueprints.SetChildOffset(child, origin);
                }

                foreach (var deco in source.Decorations)
                {
                    var child = FoundationApi.Blueprints.CreateChild(root);
                    FoundationApi.Blueprints.SetDecoration(child, deco, false, false);
                    FoundationApi.Blueprints.SetChildOffset(child, origin);
                }

                FoundationApi.Blueprints.UpdateDragPosition();
                FoundationApi.Blueprints.UpdateCostDisplay(tag.Cost());
            }
            catch
            {
                FoundationApi.WorkspaceAccess.StopBlueprintMode();
                throw;
            }
        }

        public bool CheckBeforeCommit(Component root)
        {
            if (committed)
                return false;
            try
            {
                if (player != FoundationApi.Kingdom || !FoundationApi.UiAccess.IsWorkspaceVisible || destination != FoundationApi.Kingdom.ActiveWorkspace)
                {
                    Stop("Blueprint placement cancelled because the active kingdom or workshop changed.");
                    return false;
                }

                var research = BlueprintResearch.Check(definition);
                if (!research.CanPlace)
                {
                    FoundationApi.WorkspaceAccess.StopBlueprintMode();
                    owner.ShowResearchBlocked(entryId, research);
                    return false;
                }

                var totalCost = Cost();
                if (!player.CanAfford(totalCost))
                {
                    FoundationApi.WorkspaceAccess.StopBlueprintMode();
                    FoundationApi.UiAccess.ShowInsufficientFunds();
                    return false;
                }

                var rootRect = (RectTransform)root.transform;
                var placedBoxes = new List<(int left, int right, int bottom, int top)>();
                foreach (var ghost in FoundationApi.Blueprints.Children(root))
                {
                    var element = FoundationApi.Blueprints.Building(ghost);
                    if (element == null)
                        continue;
                    var rect = (RectTransform)ghost.transform;
                    Vector2 position = rect.anchoredPosition;
                    if (ghost != root)
                        position += rootRect.anchoredPosition - new Vector2(0, rootRect.sizeDelta.y);
                    int x = Mathf.RoundToInt(position.x), y = Mathf.RoundToInt(position.y);
                    int w = Mathf.RoundToInt(rect.sizeDelta.x), h = Mathf.RoundToInt(rect.sizeDelta.y);
                    if (FoundationApi.Blueprints.Overlaps(new Vector2Int(x, y), new Vector2Int(w, h)))
                    {
                        Stop("Not enough space for the whole blueprint. No buildings were placed.");
                        return false;
                    }

                    int left = x, right = x + w, bottom = y - h, top = y;
                    foreach (var existing in placedBoxes)
                    {
                        if (left < existing.right && existing.left < right && bottom < existing.top && existing.bottom < top)
                        {
                            Stop("Buildings in this blueprint overlap each other. No buildings were placed.");
                            return false;
                        }
                    }

                    placedBoxes.Add((left, right, bottom, top));
                    FoundationApi.Blueprints.SetCollision(ghost, false);
                }

                CommitAtomically(rootRect, totalCost);
                return false;
            }
            catch (Exception error)
            {
                owner.LogLibraryError(error);
                Stop("Blueprint could not be placed. No changes were kept.");
                return false;
            }
        }

        void CommitAtomically(RectTransform rootRect, GameMoney totalCost)
        {
            if (source.Buildings.Count == 0)
                throw new InvalidOperationException("Blueprint has no buildings to place.");

            var rootPos = new Vector2Int(Mathf.RoundToInt(rootRect.anchoredPosition.x), Mathf.RoundToInt(rootRect.anchoredPosition.y));
            var delta = rootPos - source.Buildings[0].Position;
            if (!FoundationApi.WorkspaceAccess.TrySpend(totalCost))
            {
                FoundationApi.WorkspaceAccess.StopBlueprintMode();
                FoundationApi.UiAccess.ShowInsufficientFunds();
                return;
            }

            var rollback = new Stack<Action>();
            rollback.Push(() => FoundationApi.WorkspaceAccess.Grant(totalCost, false));
            try
            {
                var placed = new Dictionary<WorkspaceObject, WorkspaceObject>();
                foreach (var element in source.Buildings)
                {
                    var copy = FoundationApi.WorkspaceAccess.Copy(element);
                    copy.SetPosition(element.Position + delta);
                    FoundationApi.WorkspaceAccess.PlaceBuilding(copy);
                    rollback.Push(() =>
                    {
                        if (FoundationApi.WorkspaceAccess.IsInActiveWorkspace(copy))
                            FoundationApi.WorkspaceAccess.Remove(copy);
                    });
                    placed.Add(element, copy);
                }

                foreach (var deco in source.Decorations)
                {
                    var copy = FoundationApi.WorkspaceAccess.Copy(deco);
                    copy.Locked = deco.Locked;
                    copy.SetPosition(deco.Position + delta);
                    FoundationApi.WorkspaceAccess.PlaceDecoration(copy);
                    rollback.Push(() =>
                    {
                        if (FoundationApi.WorkspaceAccess.IsInActiveWorkspace(copy))
                            FoundationApi.WorkspaceAccess.Remove(copy);
                    });
                }

                foreach (var pipe in source.Pipes)
                {
                    var outPort = pipe.ConnectedOutput;
                    var inPort = pipe.ConnectedInput;
                    if (outPort == null || inPort == null ||
                        !placed.TryGetValue(outPort.Parent, out var outBuilding) ||
                        !placed.TryGetValue(inPort.Parent, out var inBuilding) ||
                        outPort.ParentIndex < 0 || outPort.ParentIndex >= outBuilding.Outputs.Count ||
                        inPort.ParentIndex < 0 || inPort.ParentIndex >= inBuilding.Inputs.Count)
                        throw new InvalidOperationException("Blueprint contains an invalid or incomplete pipe connection.");

                    var newPipe = FoundationApi.WorkspaceAccess.CreatePipe();
                    newPipe.Connect(outBuilding.Outputs[outPort.ParentIndex], inBuilding.Inputs[inPort.ParentIndex]);
                    if (pipe.Nodes.Count > 0)
                        newPipe.SetNodes(pipe.Nodes.Select(node => node + delta));
                    FoundationApi.WorkspaceAccess.PlacePipe(newPipe);
                    rollback.Push(() => FoundationApi.WorkspaceAccess.RemovePipe(newPipe));
                }

                committed = true;
                FoundationApi.WorkspaceAccess.StopBlueprintMode();
            }
            catch
            {
                while (rollback.Count > 0)
                {
                    try { rollback.Pop()(); }
                    catch { }
                }

                throw;
            }
        }

        public GameMoney Cost()
        {
            var total = new GameMoney();
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var element in source.Buildings)
            {
                counts.TryGetValue(element.TypeId, out int count);
                total.Add(FoundationApi.WorkspaceAccess.CopyCost(element, count));
                counts[element.TypeId] = count + Math.Max(1, element.StackSize);
            }

            foreach (var deco in source.Decorations)
                total.Add(FoundationApi.WorkspaceAccess.CopyCost(deco));
            return total;
        }

        SharableBlueprintsPlugin owner;
        PortableBlueprint definition;
        GameWorkspace source, destination;
        GameKingdom player;
        string entryId;
        bool committed;
        public static void CancelOwned(SharableBlueprintsPlugin owner)
        {
            var manager = FoundationApi.WorkspaceAccess.Root;
            if (!manager)
                return;
            var ghost = FoundationApi.Blueprints.CurrentGhost;
            var placement = ghost ? ghost.GetComponent<BlueprintPlacement>() : null;
            if (placement && placement.owner == owner)
                FoundationApi.WorkspaceAccess.StopBlueprintMode();
        }

        void Stop(string message)
        {
            FoundationApi.WorkspaceAccess.StopBlueprintMode();
            owner.ShowMessage(message);
        }
    }
}
