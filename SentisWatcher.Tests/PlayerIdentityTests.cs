using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Serialization;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.World;
using SentisWatcher.Recording;
using Xunit;

namespace SentisWatcher.Tests
{
    public sealed class PlayerIdentityTests
    {
        private const long Identity = 7001;
        private const ulong Steam = 76561198000000001;

        private static MyPlayerCollection Collection(bool mapped, ulong steam, int serial, bool npc)
        {
            // Real game lookups, with no session or online player required. Only their backing data is supplied.
            var players = (MyPlayerCollection)FormatterServices.GetUninitializedObject(typeof(MyPlayerCollection));
            var ids = new Dictionary<long, MyPlayer.PlayerId>();
            if (mapped) ids.Add(Identity, new MyPlayer.PlayerId(steam, serial));
            typeof(MyPlayerCollection).GetField("m_identityPlayerIds", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(players, ids);
            typeof(MyPlayerCollection).GetField("m_npcIdentities", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(players, npc ? new HashSet<long> { Identity } : new HashSet<long>());
            return players;
        }

        private static bool Classify(MyPlayerCollection players, long identity)
        {
            var method = typeof(Identities).GetMethod("IsPlayer", BindingFlags.Static | BindingFlags.NonPublic,
                null, new[] { typeof(MyPlayerCollection), typeof(long) }, null);
            Assert.NotNull(method);
            return (bool)method.Invoke(null, new object[] { players, identity });
        }

        [Theory]
        [InlineData(1)]
        [InlineData(17)]
        [InlineData(-1)]
        public void An_unmarked_secondary_identity_is_not_a_player_even_with_a_steam_id(int serial)
        {
            var players = Collection(true, Steam, serial, false);
            Assert.False(players.IdentityIsNpc(Identity));
            Assert.Equal(Steam, players.TryGetSteamId(Identity)); // this alone was the old classifier
            Assert.False(Classify(players, Identity));
        }

        [Fact]
        public void An_offline_primary_identity_stays_a_player_without_an_online_player_object()
        {
            // m_players is deliberately uninitialised: consulting online players would break this case.
            Assert.True(Classify(Collection(true, Steam, 0, false), Identity));
        }

        [Fact]
        public void A_registered_npc_is_not_a_player_even_when_its_id_is_primary()
        {
            Assert.False(Classify(Collection(true, Steam, 0, true), Identity));
        }

        [Fact]
        public void Nobody_missing_mapping_and_missing_session_are_not_players()
        {
            Assert.False(Classify(null, Identity));
            Assert.False(Classify(Collection(true, Steam, 0, false), 0));
            Assert.False(Classify(Collection(false, Steam, 0, false), Identity));
            Assert.False(Classify(Collection(true, 0, 0, false), Identity));
        }

        [Fact]
        public void Shared_steam_id_and_mapping_changes_are_classified_per_identity_without_stale_cache()
        {
            var players = Collection(true, Steam, 17, false);
            var ids = (Dictionary<long, MyPlayer.PlayerId>)typeof(MyPlayerCollection)
                .GetField("m_identityPlayerIds", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(players);
            ids.Add(Identity + 1, new MyPlayer.PlayerId(Steam, 0));
            Assert.False(Classify(players, Identity));
            Assert.True(Classify(players, Identity + 1));
            ids[Identity] = new MyPlayer.PlayerId(Steam, 0);
            Assert.True(Classify(players, Identity));
            ids[Identity] = new MyPlayer.PlayerId(Steam, 17);
            Assert.False(Classify(players, Identity));
        }
    }
}
