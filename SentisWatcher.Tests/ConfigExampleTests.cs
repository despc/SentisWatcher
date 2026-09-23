using System.IO;
using System.Xml.Serialization;
using SentisWatcher.Config;
using Xunit;

namespace SentisWatcher.Tests
{
    public class ConfigExampleTests
    {
        private static string ExamplePath()
        {
            var dir = new DirectoryInfo(System.AppDomain.CurrentDomain.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "SentisWatcher.cfg.example"))) dir = dir.Parent;
            Assert.NotNull(dir);
            return Path.Combine(dir.FullName, "SentisWatcher.cfg.example");
        }

        [Fact]
        public void The_example_reads_as_the_defaults()
        {
            WatcherConfig example;
            using (var stream = File.OpenRead(ExamplePath()))
                example = (WatcherConfig)new XmlSerializer(typeof(WatcherConfig)).Deserialize(stream);
            var defaults = new WatcherConfig();
            Assert.Equal(defaults.Enabled, example.Enabled);
            Assert.Equal(14, example.RetentionDays);
            Assert.Equal(defaults.RetentionDays, example.RetentionDays);
            Assert.True(string.IsNullOrEmpty(example.DatabaseFolder));
        }
    }
}
