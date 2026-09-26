using System;
using Sandbox.Game;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.World;

namespace SentisWatcher.Recording
{
    /// <summary>
    /// The planets' animals (wolves, spiders): characters with players of their own that the game spawns and removes
    /// by the dozen. They are no one's inventories, no players, no one who loads the server: left out of the
    /// inventories, the player counts and the load lists.
    /// </summary>
    public static class Wildlife
    {
        public static bool IsAnimal(MyPlayer player) => player != null && player.IsWildlifeAgent;

        /// <summary>By its player while it has one, else (dead, its player gone) by its kind.</summary>
        public static bool IsAnimal(MyCharacter character)
        {
            if (character == null) return false;
            var player = character.ControllerInfo?.Controller?.Player;
            if (player != null) return player.IsWildlifeAgent;
            return IsAnimalKind(character.Definition?.Id.SubtypeName);
        }

        public static bool IsAnimal(MyInventory inventory)
        {
            if (inventory == null) return false;
            if (inventory.IsWildlifeAgentInventory?.Value == true) return true;
            return inventory.Owner is MyCharacter character && IsAnimal(character);
        }

        /// <summary>A character subtype of an animal ("Wolf", "Space_Wolf", "Space_spider").</summary>
        public static bool IsAnimalKind(string subtype) =>
            !string.IsNullOrEmpty(subtype) &&
            (subtype.IndexOf("Wolf", StringComparison.OrdinalIgnoreCase) >= 0 || subtype.IndexOf("Spider", StringComparison.OrdinalIgnoreCase) >= 0);
    }
}
