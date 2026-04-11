using System;
using System.Collections.Generic;

namespace InfiniteDrive.Models
{
    /// <summary>
    /// Lifecycle states for a media item in the v3.3 item pipeline.
    /// Items progress through these states as they are discovered, resolved, indexed, and made playable.
    /// </summary>
    public enum ItemStatus
    {
        /// <summary>
        /// Item discovered in source manifest but not yet processed.
        /// </summary>
        Known,

        /// <summary>
        /// Stream URLs successfully resolved from upstream provider.
        /// </summary>
        Resolved,

        /// <summary>
        /// Metadata fetched from Cinemeta/AIOMetadata.
        /// </summary>
        Hydrated,

        /// <summary>
        /// .strm file written to filesystem.
        /// </summary>
        Created,

        /// <summary>
        /// Item added to Emby library and indexed.
        /// </summary>
        Indexed,

        /// <summary>
        /// Item is playable (Indexed + not removed).
        /// </summary>
        Active,

        /// <summary>
        /// Stream resolution or processing failed.
        /// </summary>
        Failed,

        /// <summary>
        /// Item removed from library.
        /// </summary>
        Deleted
    }

    /// <summary>
    /// Extension methods for ItemStatus.
    /// </summary>
    public static class ItemStatusExtensions
    {
        /// <summary>
        /// Defines valid state transitions for the item lifecycle.
        /// Key: FromState, Value: Set of allowed ToStates.
        /// </summary>
        private static readonly Dictionary<ItemStatus, HashSet<ItemStatus>> ValidTransitions = new()
        {
            { ItemStatus.Known,     new HashSet<ItemStatus> { ItemStatus.Resolved, ItemStatus.Failed, ItemStatus.Deleted } },
            { ItemStatus.Resolved,  new HashSet<ItemStatus> { ItemStatus.Hydrated, ItemStatus.Failed, ItemStatus.Deleted } },
            { ItemStatus.Hydrated,  new HashSet<ItemStatus> { ItemStatus.Created,  ItemStatus.Failed, ItemStatus.Deleted } },
            { ItemStatus.Created,   new HashSet<ItemStatus> { ItemStatus.Indexed,  ItemStatus.Failed, ItemStatus.Deleted } },
            { ItemStatus.Indexed,   new HashSet<ItemStatus> { ItemStatus.Active,   ItemStatus.Failed, ItemStatus.Deleted } },
            { ItemStatus.Active,    new HashSet<ItemStatus> { ItemStatus.Deleted,  ItemStatus.Failed } },
            { ItemStatus.Failed,    new HashSet<ItemStatus> { ItemStatus.Resolved, ItemStatus.Deleted } },
            { ItemStatus.Deleted,    new HashSet<ItemStatus>() }
        };

        /// <summary>
        /// Determines if a transition from the current state to the target state is valid.
        /// </summary>
        /// <param name="currentStatus">The current state.</param>
        /// <param name="targetStatus">The desired target state.</param>
        /// <returns>True if the transition is valid, false otherwise.</returns>
        public static bool CanTransitionTo(this ItemStatus currentStatus, ItemStatus targetStatus)
        {
            if (currentStatus == targetStatus)
                return true;

            return ValidTransitions.TryGetValue(currentStatus, out var allowedStates)
                && allowedStates.Contains(targetStatus);
        }

        /// <summary>
        /// Checks if this status represents a terminal state (no further transitions possible).
        /// </summary>
        public static bool IsTerminal(this ItemStatus status)
        {
            return status is ItemStatus.Active or ItemStatus.Deleted;
        }

        /// <summary>
        /// Checks if this status represents an error state.
        /// </summary>
        public static bool IsErrorState(this ItemStatus status)
        {
            return status == ItemStatus.Failed;
        }

        /// <summary>
        /// Returns a user-friendly display string for this status.
        /// </summary>
        public static string ToDisplayString(this ItemStatus status)
        {
            return status switch
            {
                ItemStatus.Known     => "Known",
                ItemStatus.Resolved  => "Resolved",
                ItemStatus.Hydrated  => "Hydrated",
                ItemStatus.Created   => "Created",
                ItemStatus.Indexed   => "Indexed",
                ItemStatus.Active    => "Active",
                ItemStatus.Failed    => "Failed",
                ItemStatus.Deleted   => "Deleted",
                _                   => "Unknown"
            };
        }
    }
}
