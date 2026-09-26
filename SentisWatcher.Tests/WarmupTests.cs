using System;
using SentisWatcher.Recording;
using Xunit;

namespace SentisWatcher.Tests
{
    public class WarmupTests
    {
        private static readonly DateTime Loaded = new DateTime(2026, 9, 26, 12, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void Nothing_is_charted_in_the_first_30_seconds()
        {
            Assert.False(Warmup.Passed(Loaded.AddSeconds(29.9), Loaded));
        }

        [Fact]
        public void The_charts_start_30_seconds_after_the_world_loads()
        {
            Assert.True(Warmup.Passed(Loaded.AddSeconds(30), Loaded));
        }

        [Fact]
        public void Nothing_is_charted_before_the_world_loads()
        {
            Assert.False(Warmup.Passed(Loaded, DateTime.MaxValue));
        }
    }
}

namespace SentisWatcher.Tests
{
    public class WildlifeTests
    {
        [Theory]
        [InlineData("Space_Wolf", true)]
        [InlineData("Wolf", true)]
        [InlineData("Space_spider", true)]
        [InlineData("Default_Astronaut", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void Animals_are_told_by_their_kind(string subtype, bool animal)
        {
            Assert.Equal(animal, SentisWatcher.Recording.Wildlife.IsAnimalKind(subtype));
        }
    }
}
