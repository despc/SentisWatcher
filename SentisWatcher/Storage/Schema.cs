namespace SentisWatcher.Storage
{
    /// <summary>
    /// The tables of one day file. Times are UTC milliseconds since 1970 (t). Positions have an R*Tree
    /// next to them (filled by triggers) for "what was near this point" queries; the time is filtered by
    /// the index on t.
    /// </summary>
    public static class Schema
    {
        public const int Version = 3;

        public const string Create = @"
PRAGMA journal_mode=WAL;
PRAGMA synchronous=NORMAL;

CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY, value TEXT);

-- players, grids and other entities by id: the last name, owner and steam id seen today
CREATE TABLE IF NOT EXISTS names(
  id INTEGER PRIMARY KEY, kind TEXT NOT NULL, name TEXT, owner INTEGER, steam INTEGER, t INTEGER NOT NULL);

CREATE TABLE IF NOT EXISTS player_pos(
  id INTEGER PRIMARY KEY, t INTEGER NOT NULL, identity INTEGER NOT NULL,
  x REAL, y REAL, z REAL, vx REAL, vy REAL, vz REAL,
  health REAL, controlled INTEGER, grid INTEGER);
CREATE INDEX IF NOT EXISTS player_pos_by_identity ON player_pos(identity, t);
CREATE INDEX IF NOT EXISTS player_pos_by_t ON player_pos(t);
CREATE VIRTUAL TABLE IF NOT EXISTS player_pos_space USING rtree(id, x0, x1, y0, y1, z0, z1);
CREATE TRIGGER IF NOT EXISTS player_pos_space_add AFTER INSERT ON player_pos BEGIN
  INSERT INTO player_pos_space VALUES(new.id, new.x, new.x, new.y, new.y, new.z, new.z); END;

CREATE TABLE IF NOT EXISTS grid_pos(
  id INTEGER PRIMARY KEY, t INTEGER NOT NULL, grid INTEGER NOT NULL,
  x REAL, y REAL, z REAL, fx REAL, fy REAL, fz REAL, ux REAL, uy REAL, uz REAL,
  vx REAL, vy REAL, vz REAL, blocks INTEGER, owner INTEGER, static INTEGER, radius REAL);
CREATE INDEX IF NOT EXISTS grid_pos_by_grid ON grid_pos(grid, t);
CREATE INDEX IF NOT EXISTS grid_pos_by_t ON grid_pos(t);
CREATE VIRTUAL TABLE IF NOT EXISTS grid_pos_space USING rtree(id, x0, x1, y0, y1, z0, z1);
CREATE TRIGGER IF NOT EXISTS grid_pos_space_add AFTER INSERT ON grid_pos BEGIN
  INSERT INTO grid_pos_space VALUES(new.id, new.x - new.radius, new.x + new.radius,
    new.y - new.radius, new.y + new.radius, new.z - new.radius, new.z + new.radius); END;

-- what happened: kind, who (identity), to what (entity), where; amount/count for aggregated kinds
CREATE TABLE IF NOT EXISTS events(
  id INTEGER PRIMARY KEY, t INTEGER NOT NULL, kind TEXT NOT NULL, actor INTEGER, entity INTEGER,
  x REAL, y REAL, z REAL, amount REAL, count INTEGER, detail TEXT);
CREATE INDEX IF NOT EXISTS events_by_t ON events(t);
CREATE INDEX IF NOT EXISTS events_by_actor ON events(actor, t);
CREATE INDEX IF NOT EXISTS events_by_entity ON events(entity, t);

-- the content of an inventory whenever it changed (and once at the start of the day); flows: where the
-- change since its previous row came from and went to ('refine|Ingot/Iron:+4800;...'); items NULL: gone
CREATE TABLE IF NOT EXISTS inventories(
  id INTEGER PRIMARY KEY, t INTEGER NOT NULL, entity INTEGER NOT NULL, inv INTEGER NOT NULL,
  grid INTEGER, owner INTEGER, items TEXT, volume REAL, max_volume REAL, flows TEXT);
CREATE INDEX IF NOT EXISTS inventories_by_entity ON inventories(entity, inv, t);
CREATE INDEX IF NOT EXISTS inventories_by_grid ON inventories(grid, t);
CREATE INDEX IF NOT EXISTS inventories_by_owner ON inventories(owner, t);

-- the planets of the world (written at the start of a day and of a server run), for the web view's map
CREATE TABLE IF NOT EXISTS planets(
  id INTEGER PRIMARY KEY, name TEXT, generator TEXT, x REAL, y REAL, z REAL,
  radius REAL, min_radius REAL, max_radius REAL, atmosphere REAL, gravity REAL, t INTEGER NOT NULL);

-- anomalies, also written to the log
CREATE TABLE IF NOT EXISTS alerts(
  id INTEGER PRIMARY KEY, t INTEGER NOT NULL, kind TEXT NOT NULL, actor INTEGER, entity INTEGER, detail TEXT);
CREATE INDEX IF NOT EXISTS alerts_by_t ON alerts(t);

-- how hard the server worked, every 5 s: the game thread's frame and its physics in ms (average and worst frame
-- of the period), the other parts of the frame in blocks ('entities_before:avg:max;entities_after:...;other:...'),
-- collections by generation, the share of the time in GC (%), memory (MB), the simulation speed, players online
CREATE TABLE IF NOT EXISTS perf(
  id INTEGER PRIMARY KEY, t INTEGER NOT NULL, frames INTEGER, frame REAL, frame_max REAL, physics REAL, physics_max REAL,
  gc0 INTEGER, gc1 INTEGER, gc2 INTEGER, gc_time REAL, managed_mb REAL, private_mb REAL, working_mb REAL,
  sim REAL, players INTEGER, blocks TEXT);
CREATE INDEX IF NOT EXISTS perf_by_t ON perf(t);
CREATE TABLE IF NOT EXISTS load(
  id INTEGER PRIMARY KEY, t INTEGER NOT NULL, kind TEXT NOT NULL, entity INTEGER, name TEXT, owner INTEGER, owner_name TEXT,
  frames INTEGER, ms REAL, max_ms REAL);
CREATE INDEX IF NOT EXISTS load_by_t ON load(t);
";

        /// <summary>Changes to the files of older versions, each run only where it is missing.</summary>
        public static readonly (string Table, string Column, string Alter)[] Added =
        {
            ("inventories", "flows", "ALTER TABLE inventories ADD COLUMN flows TEXT"),
        };
    }
}
