using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;

using Server.Commands;
using Server.Items;

namespace Server.Engines.CharacterStates
{
    /// <summary>
    ///     One state file, as the listing shows it.
    /// </summary>
    public class StateInfo
    {
        public string Name { get; set; }
        public DateTime Saved { get; set; }
        public string From { get; set; }
        public long Size { get; set; }
    }

    /// <summary>
    ///     Saves a character to a named XML file and restores a character from one, so an
    ///     end-to-end test starts from the same state every time.
    ///     <para />
    ///     A state file carries no identity: no name, no account, no serial and no appearance. So
    ///     any character can restore any state, and several bots can hold one state at the same
    ///     time. Each restore builds fresh items.
    ///     <para />
    ///     Every method here assumes it is running on the game thread. Mobiles are not safe to
    ///     touch from anywhere else.
    /// </summary>
    public static class CharacterState
    {
        private const int FileVersion = 1;

        public const string NameRule = "A state name accepts letters, digits, a dash and an underscore, up to 40 of them.";

        /// <summary>
        ///     The character fields a state carries, in the order a restore applies them. The stats
        ///     come first, because the vitals cannot exceed a maximum that a stat sets.
        /// </summary>
        private static readonly string[] _Fields =
        {
            "RawStr", "RawDex", "RawInt", "StatCap",
            "Hits", "Stam", "Mana",
            "Hunger", "Thirst", "BAC",
            "Fame", "Karma", "Kills", "ShortTermMurders", "Criminal",
            "Warmode", "Hidden", "Blessed"
        };

        /// <summary>
        ///     Item properties the restore sets by itself, from the element instead of a value.
        /// </summary>
        private static readonly string[] _NotProperties = {"Parent", "Map", "Location", "Layer", "X", "Y", "Z"};

        private static readonly Dictionary<Type, PropertyInfo[]> _Writable = new Dictionary<Type, PropertyInfo[]>();

        public static bool Enabled { get { return Config.Get("CharacterState.Enabled", true); } }

        public static string Folder
        {
            get { return Path.Combine(Core.BaseDirectory, Config.Get("CharacterState.Folder", "States")); }
        }

        #region Store

        public static bool IsValidName(string name)
        {
            if (String.IsNullOrEmpty(name) || name.Length > 40)
            {
                return false;
            }

            return name.All(c => Char.IsLetterOrDigit(c) || c == '-' || c == '_');
        }

        public static bool Exists(string name)
        {
            return IsValidName(name) && File.Exists(PathFor(name));
        }

        public static List<StateInfo> List()
        {
            var states = new List<StateInfo>();

            if (!Directory.Exists(Folder))
            {
                return states;
            }

            foreach (var file in Directory.GetFiles(Folder, "*.xml"))
            {
                var info = new StateInfo
                {
                    Name = Path.GetFileNameWithoutExtension(file),
                    Size = new FileInfo(file).Length
                };

                try
                {
                    var root = XDocument.Load(file).Root;

                    if (root != null)
                    {
                        DateTime saved;

                        if (DateTime.TryParse(
                            (string)root.Attribute("saved"),
                            CultureInfo.InvariantCulture,
                            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                            out saved))
                        {
                            info.Saved = saved;
                        }

                        info.From = (string)root.Attribute("from");
                    }
                }
                catch
                {
                    info.From = "unreadable";
                }

                states.Add(info);
            }

            return states.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        public static string Remove(string name)
        {
            if (!IsValidName(name))
            {
                return NameRule;
            }

            var path = PathFor(name);

            if (!File.Exists(path))
            {
                return String.Format("There is no state named {0}.", name);
            }

            try
            {
                File.Delete(path);
            }
            catch (Exception e)
            {
                return "The state could not be deleted: " + e.Message;
            }

            return null;
        }

        private static string PathFor(string name)
        {
            return Path.Combine(Folder, name + ".xml");
        }

        #endregion

        #region Save

        /// <summary>
        ///     Writes <paramref name="m" /> to the state <paramref name="name" />. Returns null when
        ///     the state is written, or the reason it is not. Anything left out lands in
        ///     <paramref name="notes" />.
        /// </summary>
        public static string Save(Mobile m, string name, List<string> notes)
        {
            if (!Enabled)
            {
                return "Character states are turned off.";
            }

            if (m == null || m.Deleted)
            {
                return "That character does not exist.";
            }

            if (!IsValidName(name))
            {
                return NameRule;
            }

            var root = new XElement(
                "characterstate",
                new XAttribute("version", FileVersion),
                new XAttribute("saved", DateTime.UtcNow.ToString("u", CultureInfo.InvariantCulture)),
                new XAttribute("from", m.Name ?? String.Empty));

            root.Add(SavePosition(m));
            root.Add(SaveFields(m, notes));
            root.Add(SaveSkills(m));
            root.Add(SaveItems(m, notes));

            try
            {
                Directory.CreateDirectory(Folder);

                var path = PathFor(name);
                var temp = path + ".tmp";

                new XDocument(root).Save(temp);

                if (File.Exists(path))
                {
                    File.Delete(path);
                }

                File.Move(temp, path);
            }
            catch (Exception e)
            {
                return "The state could not be written: " + e.Message;
            }

            return null;
        }

        private static XElement SavePosition(Mobile m)
        {
            var location = m.Location;
            var map = m.Map;

            // A logged-out character sits on the internal map, and the login puts it back from the
            // logout fields. So those hold the position that matters, not the live one.
            if ((map == null || map == Map.Internal) && m.LogoutMap != null)
            {
                location = m.LogoutLocation;
                map = m.LogoutMap;
            }

            return new XElement(
                "position",
                new XAttribute("x", location.X),
                new XAttribute("y", location.Y),
                new XAttribute("z", location.Z),
                new XAttribute("map", map == null ? Map.Internal.Name : map.Name),
                new XAttribute("direction", m.Direction & Direction.Mask));
        }

        private static XElement SaveFields(Mobile m, List<string> notes)
        {
            var root = new XElement("fields");

            foreach (var field in _Fields)
            {
                var p = Field(m.GetType(), field);

                if (p == null)
                {
                    notes.Add(String.Format("{0} is no longer a character field.", field));
                    continue;
                }

                string text;

                if (Read(m, p, out text))
                {
                    root.Add(new XElement("set", new XAttribute("name", p.Name), text));
                }
            }

            return root;
        }

        private static XElement SaveSkills(Mobile m)
        {
            var root = new XElement("skills");

            foreach (SkillName name in Enum.GetValues(typeof(SkillName)))
            {
                var skill = m.Skills[name];

                if (skill == null)
                {
                    continue;
                }

                root.Add(
                    new XElement(
                        "skill",
                        new XAttribute("name", name),
                        new XAttribute("base", skill.Base.ToString("0.0", CultureInfo.InvariantCulture)),
                        new XAttribute("cap", skill.Cap.ToString("0.0", CultureInfo.InvariantCulture)),
                        new XAttribute("lock", skill.Lock)));
            }

            return root;
        }

        private static XElement SaveItems(Mobile m, List<string> notes)
        {
            var root = new XElement("items");
            var probes = new Dictionary<Type, Item[]>();

            try
            {
                foreach (var item in m.Items.ToArray())
                {
                    var element = SaveItem(item, true, probes, notes);

                    if (element != null)
                    {
                        root.Add(element);
                    }
                }
            }
            finally
            {
                foreach (var probe in probes.Values.Where(p => p != null).SelectMany(p => p))
                {
                    probe.Delete();
                }
            }

            return root;
        }

        private static XElement SaveItem(Item item, bool equipped, Dictionary<Type, Item[]> probes, List<string> notes)
        {
            var type = item.GetType();
            var fresh = Probe(type, probes);

            if (fresh == null)
            {
                notes.Add(String.Format("{0} cannot be built again, so it was left out.", type.Name));
                return null;
            }

            var element = new XElement("item", new XAttribute("type", type.FullName));

            if (equipped)
            {
                element.Add(new XAttribute("layer", item.Layer));
            }
            else
            {
                element.Add(new XAttribute("x", item.X));
                element.Add(new XAttribute("y", item.Y));
            }

            // Only what differs from a fresh item of the same type. So a hatchet writes one line
            // instead of fifty, and a reader sees what the state actually decides.
            //
            // Two fresh items answer the question, not one: a constructor that rolls a value, as a
            // weapon rolls its durability, makes a one-item comparison a coin toss. A property that
            // the two fresh items disagree on is never a default, so it always goes in the file.
            foreach (var p in Writable(type))
            {
                string mine, first, second;

                if (!Read(item, p, out mine) || !Read(fresh[0], p, out first) || !Read(fresh[1], p, out second))
                {
                    continue;
                }

                if (first == second && mine == first)
                {
                    continue;
                }

                element.Add(new XElement("set", new XAttribute("name", p.Name), mine));
            }

            var container = item as Container;

            if (container != null && container.Items.Count > 0)
            {
                var children = new XElement("items");

                foreach (var child in container.Items.ToArray())
                {
                    var childElement = SaveItem(child, false, probes, notes);

                    if (childElement != null)
                    {
                        children.Add(childElement);
                    }
                }

                element.Add(children);
            }

            return element;
        }

        /// <summary>
        ///     Two fresh instances per type, to compare against, or null when the type cannot be
        ///     built. The caller deletes them.
        /// </summary>
        private static Item[] Probe(Type type, Dictionary<Type, Item[]> probes)
        {
            Item[] pair;

            if (probes.TryGetValue(type, out pair))
            {
                return pair;
            }

            Item first = null, second = null;

            try
            {
                first = Activator.CreateInstance(type, true) as Item;
                second = Activator.CreateInstance(type, true) as Item;
            }
            catch
            { }

            pair = first != null && second != null ? new[] {first, second} : null;

            if (pair == null)
            {
                // One of the two may still have been built, and nothing else will delete it.
                if (first != null)
                {
                    first.Delete();
                }

                if (second != null)
                {
                    second.Delete();
                }
            }

            probes[type] = pair;

            return pair;
        }

        #endregion

        #region Load

        /// <summary>
        ///     Rebuilds <paramref name="m" /> from the state <paramref name="name" />. Returns null
        ///     when the character is restored, or the reason it is not. A refusal changes nothing.
        /// </summary>
        public static string Load(Mobile m, string name, List<string> notes)
        {
            if (!Enabled)
            {
                return "Character states are turned off.";
            }

            if (m == null || m.Deleted)
            {
                return "That character does not exist.";
            }

            if (!IsValidName(name))
            {
                return NameRule;
            }

            var path = PathFor(name);

            if (!File.Exists(path))
            {
                return String.Format("There is no state named {0}.", name);
            }

            XElement root;

            try
            {
                root = XDocument.Load(path).Root;
            }
            catch (Exception e)
            {
                return "The state could not be read: " + e.Message;
            }

            if (root == null || root.Name != "characterstate")
            {
                return String.Format("{0} is not a character state.", name);
            }

            var version = (int?)root.Attribute("version") ?? 0;

            if (version > FileVersion)
            {
                return String.Format("{0} comes from a newer server.", name);
            }

            // Everything above can refuse. From here the character changes, so nothing below may.
            foreach (var item in m.Items.ToArray())
            {
                item.Delete();
            }

            LoadFields(m, root.Element("fields"), notes);
            LoadSkills(m, root.Element("skills"), notes);
            LoadItems(m, root.Element("items"), notes);
            LoadPosition(m, root.Element("position"), notes);

            return null;
        }

        private static void LoadFields(Mobile m, XElement root, List<string> notes)
        {
            if (root == null)
            {
                return;
            }

            var given = root.Elements("set").ToList();

            // The code owns the order, not the file: a vital cannot be set before its stat.
            foreach (var field in _Fields)
            {
                var element = given.FirstOrDefault(e => Insensitive.Equals((string)e.Attribute("name"), field));

                if (element == null)
                {
                    continue;
                }

                given.Remove(element);

                var p = Field(m.GetType(), field);

                if (p == null)
                {
                    notes.Add(String.Format("{0} is no longer a character field.", field));
                    continue;
                }

                var error = Write(m, p, element.Value);

                if (error != null)
                {
                    notes.Add(String.Format("{0}: {1}", field, error));
                }
            }

            foreach (var element in given)
            {
                notes.Add(String.Format("{0} is not a character field and was ignored.", (string)element.Attribute("name")));
            }
        }

        private static void LoadSkills(Mobile m, XElement root, List<string> notes)
        {
            if (root == null)
            {
                return;
            }

            foreach (var element in root.Elements("skill"))
            {
                var given = (string)element.Attribute("name");

                SkillName name;

                if (!Enum.TryParse(given, true, out name))
                {
                    notes.Add(String.Format("{0} is not a skill and was ignored.", given));
                    continue;
                }

                var skill = m.Skills[name];

                if (skill == null)
                {
                    continue;
                }

                double value;

                if (Number(element.Attribute("cap"), out value))
                {
                    skill.Cap = value;
                }

                if (Number(element.Attribute("base"), out value))
                {
                    skill.BaseFixedPoint = (int)Math.Round(value * 10.0);
                }

                SkillLock locked;

                if (Enum.TryParse((string)element.Attribute("lock"), true, out locked))
                {
                    skill.SetLockNoRelay(locked);
                }
            }
        }

        private static void LoadItems(object parent, XElement root, List<string> notes)
        {
            if (root == null)
            {
                return;
            }

            foreach (var element in root.Elements("item"))
            {
                var typeName = (string)element.Attribute("type");
                var type = String.IsNullOrEmpty(typeName) ? null : ScriptCompiler.FindTypeByFullName(typeName);

                if (type == null || !typeof(Item).IsAssignableFrom(type))
                {
                    notes.Add(String.Format("{0} is not an item type and was left out.", typeName));
                    continue;
                }

                Item item;

                try
                {
                    item = Activator.CreateInstance(type, true) as Item;
                }
                catch
                {
                    item = null;
                }

                if (item == null)
                {
                    notes.Add(String.Format("{0} cannot be built and was left out.", type.Name));
                    continue;
                }

                // The values go in before the item hangs anywhere. A container decides an item's
                // grid slot as it goes in, and it counts the item itself as an occupant, so a slot
                // set afterwards always reads as taken and the item moves one along.
                LoadValues(item, type, element, notes);

                var mobile = parent as Mobile;

                if (mobile != null)
                {
                    Layer layer;

                    if (Enum.TryParse((string)element.Attribute("layer"), true, out layer))
                    {
                        item.Layer = layer;
                    }

                    mobile.AddItem(item);
                }
                else
                {
                    var container = (Container)parent;

                    item.Location = new Point3D((int?)element.Attribute("x") ?? 0, (int?)element.Attribute("y") ?? 0, 0);

                    container.AddItem(item);
                }

                var children = element.Element("items");

                if (children == null)
                {
                    continue;
                }

                if (item is Container)
                {
                    LoadItems(item, children, notes);
                }
                else
                {
                    notes.Add(String.Format("{0} is not a container, so its contents were left out.", type.Name));
                }
            }
        }

        /// <summary>
        ///     Applies the values of one item, in two passes. A property can clamp itself to another
        ///     one, as a weapon clamps its durability to the maximum, and the file has no order that
        ///     would put the maximum first. Whatever two passes cannot place lands in
        ///     <paramref name="notes" />, so nothing goes missing in silence.
        /// </summary>
        private static void LoadValues(Item item, Type type, XElement element, List<string> notes)
        {
            var values = new List<KeyValuePair<PropertyInfo, string>>();

            foreach (var set in element.Elements("set"))
            {
                var given = (string)set.Attribute("name");
                var p = Writable(type).FirstOrDefault(w => Insensitive.Equals(w.Name, given));

                if (p == null)
                {
                    notes.Add(String.Format("{0} has no property {1}.", type.Name, given));
                    continue;
                }

                values.Add(new KeyValuePair<PropertyInfo, string>(p, set.Value));
            }

            for (var pass = 0; pass < 2; ++pass)
            {
                foreach (var value in values)
                {
                    if (Holds(item, value))
                    {
                        continue;
                    }

                    Write(item, value.Key, value.Value);
                }
            }

            foreach (var value in values)
            {
                if (!Holds(item, value))
                {
                    string current;
                    Read(item, value.Key, out current);

                    notes.Add(
                        String.Format(
                            "{0}.{1} stayed {2} instead of {3}.", type.Name, value.Key.Name, current ?? "unreadable", value.Value));
                }
            }
        }

        private static bool Holds(Item item, KeyValuePair<PropertyInfo, string> value)
        {
            string current;

            return Read(item, value.Key, out current) && current == value.Value;
        }

        private static void LoadPosition(Mobile m, XElement root, List<string> notes)
        {
            if (root == null)
            {
                return;
            }

            var mapName = (string)root.Attribute("map");
            var map = String.IsNullOrEmpty(mapName) ? null : Map.Parse(mapName);

            if (map == null || map == Map.Internal)
            {
                notes.Add(String.Format("{0} is not a place, so the character stayed where it was.", mapName));
                return;
            }

            var location = new Point3D(
                (int?)root.Attribute("x") ?? 0,
                (int?)root.Attribute("y") ?? 0,
                (int?)root.Attribute("z") ?? 0);

            Direction direction;

            if (Enum.TryParse((string)root.Attribute("direction"), true, out direction))
            {
                m.Direction = direction;
            }

            // The login reads these, so a restore that only moves the body loses the position the
            // moment the character logs in.
            m.LogoutLocation = location;
            m.LogoutMap = map;

            if (m.NetState != null)
            {
                m.MoveToWorld(location, map);
                return;
            }

            // The server keeps a logged-out character standing in the world for five minutes, and
            // its own timer takes the body out when that time is up. A restored character has no
            // business standing at the place it came from, so the body goes now. The timer then
            // finds the character internalized and leaves the fields above alone.
            if (m.Map != Map.Internal)
            {
                EventSink.InvokeLogout(new LogoutEventArgs(m));
                m.Internalize();
            }
        }

        #endregion

        #region Values

        /// <summary>
        ///     The properties of an item a state carries: everything the property gump can edit,
        ///     minus what the element itself decides.
        /// </summary>
        private static PropertyInfo[] Writable(Type type)
        {
            PropertyInfo[] cached;

            if (_Writable.TryGetValue(type, out cached))
            {
                return cached;
            }

            cached = type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
                         .Where(p => p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0)
                         .Where(p => Array.IndexOf(_NotProperties, p.Name) < 0)
                         .Where(p =>
                         {
                             var cpa = Properties.GetCPA(p);

                             return cpa != null && !cpa.ReadOnly;
                         })
                         .OrderBy(p => p.Name, StringComparer.Ordinal)
                         .ToArray();

            _Writable[type] = cached;

            return cached;
        }

        private static PropertyInfo Field(Type type, string name)
        {
            return type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
        }

        /// <summary>
        ///     Formats a value so that <see cref="Write" /> reads it back. The server runs on the
        ///     invariant culture, and a number goes out the same way, so a decimal point stays a
        ///     decimal point.
        /// </summary>
        private static bool Read(object o, PropertyInfo p, out string text)
        {
            text = null;

            object value;

            try
            {
                value = p.GetValue(o, null);
            }
            catch
            {
                return false;
            }

            if (value == null)
            {
                text = "(-null-)";
            }
            else if (value is string)
            {
                text = (string)value;
            }
            else if (value is Enum || !(value is IConvertible))
            {
                text = value.ToString();
            }
            else
            {
                text = Convert.ToString(value, CultureInfo.InvariantCulture);
            }

            return true;
        }

        /// <summary>
        ///     Sets one property from text, through the parser behind the property gump. Returns
        ///     null when the value is set, or the reason it is not.
        /// </summary>
        private static string Write(object o, PropertyInfo p, string text)
        {
            object value = null;

            if (text == "(-null-)" && !p.PropertyType.IsValueType)
            {
                // ConstructFromString hands a null to a parsable type, which throws on it.
                value = null;
            }
            else
            {
                var error = Properties.ConstructFromString(p.PropertyType, o, text, ref value);

                if (error != null)
                {
                    return error;
                }
            }

            try
            {
                p.SetValue(o, value, null);
            }
            catch (Exception e)
            {
                return e.Message;
            }

            return null;
        }

        private static bool Number(XAttribute attribute, out double value)
        {
            return Double.TryParse(
                (string)attribute,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out value);
        }

        #endregion
    }
}
