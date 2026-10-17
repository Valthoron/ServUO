using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

using Server.Engines.Craft;
using Server.Engines.Harvest;
using Server.Items;
using Server.Mobiles;

namespace Server.Engines.CraftExport
{
    // It builds items of every type it describes, and one of every vendor, to read what only an
    // instance knows, and deletes each again. So it runs on the game thread, once per server run:
    // the facts change only with a new build.
    public static class CraftExport
    {
        private static Dictionary<string, string> _Files;

        public static string GetFile(string name)
        {
            if (_Files == null)
            {
                _Files = new Exporter().Run();
            }

            string text;

            return _Files.TryGetValue(name, out text) ? text : null;
        }

        private sealed class CraftInfo
        {
            public readonly string Name;
            public readonly CraftSystem System;
            public readonly string Station;
            public readonly string Book;

            public CraftInfo(string name, CraftSystem system, string station, string book)
            {
                Name = name;
                System = system;
                Station = station;
                Book = book;
            }
        }

        private sealed class HarvestInfo
        {
            public readonly string Name;
            public readonly HarvestSystem System;

            public HarvestInfo(string name, HarvestSystem system)
            {
                Name = name;
                System = system;
            }
        }

        private sealed class Facts
        {
            public readonly SortedSet<int> Graphics = new SortedSet<int>();
            public readonly SortedSet<int> Hues = new SortedSet<int>();
            public readonly SortedSet<int> Names = new SortedSet<int>();
            public int Weight;
            public string Label;
        }

        // A kind's name is settled only once every kind is known, so a file names a kind by a
        // placeholder until then.
        private sealed class Kind
        {
            public int Id;
            public string Label;
            public string Name;
            public string Source;
            public Type Type;
            public readonly SortedSet<int> Graphics = new SortedSet<int>();
            public readonly SortedSet<int> ShopGraphics = new SortedSet<int>();
            public readonly SortedSet<int> Hues = new SortedSet<int>();
            public readonly SortedSet<int> Names = new SortedSet<int>();
            public int Weight;
            public bool TakesMaterialHue;
            public HashSet<Kind> Members;

            public bool IsGroup { get { return Members != null; } }
        }

        private sealed class VendorInfo
        {
            public string Name;
            public string Title;
            public readonly HashSet<Kind> Sells = new HashSet<Kind>();
            public readonly HashSet<Kind> Buys = new HashSet<Kind>();
        }

        private sealed class Exporter
        {
            // A type that picks its graphic or its hue at random, as BaseOre and Fish do, shows each
            // only on some builds. Ore picks its commonest size 75 times in 100 (BaseOre.RandomSize),
            // so 12 builds showed only that one in about 3 exports of 100. Shimmering crystals pick
            // one of 36 graphics, and 100 builds missed at least one in about 9 exports of 10.
            private const int FirstBuilds = 30;
            private const int VariedBuilds = 500;

            // The station and the book of a system live in its CanCraft, as code: DefBlacksmithy
            // wants an anvil and a forge, DefGlassblowing a forge and the Glassblowing flag, and
            // DefMasonry the Masonry flag.
            private readonly CraftInfo[] _Crafts =
            {
                new CraftInfo("alchemy", DefAlchemy.CraftSystem, null, null),
                new CraftInfo("blacksmithy", DefBlacksmithy.CraftSystem, "anvil-and-forge", null),
                new CraftInfo("bowcraft", DefBowFletching.CraftSystem, null, null),
                new CraftInfo("carpentry", DefCarpentry.CraftSystem, null, null),
                new CraftInfo("cartography", DefCartography.CraftSystem, null, null),
                new CraftInfo("cooking", DefCooking.CraftSystem, null, null),
                new CraftInfo("glassblowing", DefGlassblowing.CraftSystem, "forge", "glassblowing"),
                new CraftInfo("inscription", DefInscription.CraftSystem, null, null),
                new CraftInfo("masonry", DefMasonry.CraftSystem, null, "masonry"),
                new CraftInfo("tailoring", DefTailoring.CraftSystem, null, null),
                new CraftInfo("tinkering", DefTinkering.CraftSystem, null, null)
            };

            private readonly HarvestInfo[] _Harvests =
            {
                new HarvestInfo("lumberjacking", Lumberjacking.System),
                new HarvestInfo("mining", Mining.System),
                new HarvestInfo("fishing", Fishing.System)
            };

            private readonly Clilocs _Clilocs = Clilocs.Load();
            private readonly List<Type> _AllTypes = GetAllTypes();

            // ObjectPropertyList.Add(string) wraps a text in 1042971 or 1070722, and
            // Item.AddNameProperty a stack's name in 1050039. None names an item by itself.
            private static readonly int[] _WrapperClilocs = { 1042971, 1070722, 1050039 };

            private readonly Dictionary<Type, Facts> _Facts = new Dictionary<Type, Facts>();
            private readonly Dictionary<BeverageType, Facts> _BeverageFacts = new Dictionary<BeverageType, Facts>();
            private readonly List<Kind> _Kinds = new List<Kind>();
            private readonly Dictionary<string, Kind> _KindsBySource = new Dictionary<string, Kind>();
            private readonly Dictionary<string, Kind> _Groups = new Dictionary<string, Kind>();
            private readonly List<string> _Problems = new List<string>();

            public Dictionary<string, string> Run()
            {
                AddCraftTools();
                AddHarvestTools();

                var materials = WriteMaterials();
                var recipes = WriteRecipes();
                var harvests = WriteHarvests();
                var vendors = GetVendors();
                var stations = WriteStations();

                AddFamilies();
                SettleNames();

                return new Dictionary<string, string>
                {
                    { "items.toml", WriteItems() },
                    { "materials.toml", FillNames(materials) },
                    { "recipes.toml", FillNames(recipes) },
                    { "harvests.toml", FillNames(harvests) },
                    { "vendors.toml", WriteVendors(vendors) },
                    { "stations.toml", stations }
                };
            }

            #region Names

            private const char PlaceholderStart = '\u0001';
            private const char QuotedPlaceholderStart = '\u0002';
            private const char PlaceholderEnd = '\u0003';

            private static string GetPlaceholder(Kind kind)
            {
                return PlaceholderStart + kind.Id.ToString(CultureInfo.InvariantCulture) + PlaceholderEnd;
            }

            private static string QuoteKind(Kind kind)
            {
                return kind == null ? "\"\"" : QuotedPlaceholderStart + kind.Id.ToString(CultureInfo.InvariantCulture) + PlaceholderEnd;
            }

            private string FillNames(string text)
            {
                return Regex.Replace(text, "([\u0001\u0002])([0-9]+)\u0003", m =>
                {
                    var name = _Kinds[Int32.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture)].Name;

                    return m.Groups[1].Value[0] == QuotedPlaceholderStart ? Quote(name) : name;
                });
            }

            // A kind takes the label a player reads. Where types share a label, the one whose class
            // is named like the label keeps it, as FishingPole keeps fishing-pole, and the others
            // take their class names.
            private void SettleNames()
            {
                var taken = new HashSet<string>(_Groups.Keys);

                foreach (var sharing in _Kinds.Where(k => !k.IsGroup).GroupBy(k => k.Label).OrderBy(g => g.Key, StringComparer.Ordinal))
                {
                    var label = sharing.Key;
                    var kinds = sharing.OrderBy(k => k.Type.FullName, StringComparer.Ordinal).ToList();

                    Kind owner = null;

                    if (!taken.Contains(label))
                    {
                        owner = kinds.Count == 1 ? kinds[0] : kinds.FirstOrDefault(k => GetClassName(k.Type) == label);
                    }

                    if (owner != null)
                    {
                        owner.Name = label;
                        taken.Add(label);
                    }

                    foreach (var kind in kinds.Where(k => k != owner))
                    {
                        var className = GetClassName(kind.Type);
                        var name = taken.Contains(className) ? label + "-" + className : className;
                        var unique = name;

                        for (var i = 2; taken.Contains(unique); i++)
                        {
                            unique = name + "-" + i.ToString(CultureInfo.InvariantCulture);
                        }

                        kind.Name = unique;
                        taken.Add(unique);

                        _Problems.Add(String.Format("{0} shares the label {1} with another type, so its kind is {2}.", kind.Type.Name, label, unique));
                    }
                }
            }

            private static string GetClassName(Type type)
            {
                return ToName(SplitWords(type.Name));
            }

            #endregion

            #region Items

            private string WriteItems()
            {
                var text = new StringBuilder();

                text.AppendLine("# Generated by the craft export of the Celephais server. Export again rather than edit.");
                text.AppendLine("#");
                text.AppendLine("# One kind for every product, ingredient, material, tool, harvest and vendor good, named");
                text.AppendLine("# after the label a player reads. A hue is given only where another kind shares a graphic");
                text.AppendLine("# under a different hue, and never for a type that picks its hue at random. A product");
                text.AppendLine("# whose recipe takes a material has none either: the material sets it.");
                text.AppendLine("#");
                text.AppendLine("# The shop graphics are those a vendor shows the kind under in a buy window, where they");
                text.AppendLine("# differ from its own. The names are the clilocs of the item's name line, and of the buy");
                text.AppendLine("# window, given where another kind shares a graphic, whatever its hue.");
                text.AppendLine("#");
                text.AppendLine("# A beverage kind holds every vessel full of one drink, as water does, and an empty");
                text.AppendLine("# vessel has a kind of its own, as glass-pitcher does.");
                text.AppendLine("#");
                text.AppendLine("# A group lists the kinds it holds: the tools of a skill, what a harvest yields, what a");
                text.AppendLine("# craft makes, and as any-<class> every kind whose type derives from a class.");

                var byGraphic = GetKindsByGraphic(false);
                var byShownGraphic = GetKindsByGraphic(true);
                var isNamed = new HashSet<Kind>();

                foreach (var kind in _Kinds.Where(k => !k.IsGroup).OrderBy(k => k.Name, StringComparer.Ordinal))
                {
                    if (!kind.Graphics.Concat(kind.ShopGraphics).Any(g => byShownGraphic[g].Count > 1))
                    {
                        continue;
                    }

                    if (kind.Names.Count == 0)
                    {
                        _Problems.Add(String.Format("{0} shares a graphic with another kind, and has no cliloc on its name line to tell them apart.", kind.Name));
                        continue;
                    }

                    isNamed.Add(kind);
                }

                foreach (var problem in _Problems)
                {
                    text.AppendLine("# " + problem);
                }

                foreach (var kind in _Kinds.OrderBy(k => k.Name, StringComparer.Ordinal))
                {
                    text.AppendLine();
                    text.AppendLine("# " + kind.Source);
                    text.AppendLine("[" + kind.Name + "]");

                    if (kind.IsGroup)
                    {
                        text.AppendLine("kinds = [" + String.Join(", ", GetSortedNames(kind.Members).Select(Quote)) + "]");
                        continue;
                    }

                    text.AppendLine("graphics = [" + FormatGraphics(kind.Graphics) + "]");

                    if (kind.ShopGraphics.Count > 0)
                    {
                        text.AppendLine("shop-graphics = [" + FormatGraphics(kind.ShopGraphics) + "]");
                    }

                    if (isNamed.Contains(kind))
                    {
                        text.AppendLine("names = [" + String.Join(", ", kind.Names.Select(n => n.ToString(CultureInfo.InvariantCulture))) + "]");
                    }

                    if (HasFixedHue(kind, byGraphic))
                    {
                        text.AppendLine("hue = " + kind.Hues.First().ToString(CultureInfo.InvariantCulture));
                    }

                    if (kind.Weight > 0)
                    {
                        text.AppendLine("weight = " + kind.Weight.ToString(CultureInfo.InvariantCulture));
                    }
                }

                return text.ToString();
            }

            private static bool HasFixedHue(Kind kind, Dictionary<int, List<Kind>> byGraphic)
            {
                if (kind.TakesMaterialHue || kind.Hues.Count != 1)
                {
                    return false;
                }

                var hue = kind.Hues.First();

                return kind.Graphics.Any(g => byGraphic[g].Any(other => !other.Hues.SetEquals(new[] { hue })));
            }

            private Dictionary<int, List<Kind>> GetKindsByGraphic(bool includeShopGraphics)
            {
                var byGraphic = new Dictionary<int, List<Kind>>();

                foreach (var kind in _Kinds.Where(k => !k.IsGroup))
                {
                    var graphics = includeShopGraphics ? kind.Graphics.Union(kind.ShopGraphics) : kind.Graphics;

                    foreach (var graphic in graphics)
                    {
                        List<Kind> sharing;

                        if (!byGraphic.TryGetValue(graphic, out sharing))
                        {
                            byGraphic[graphic] = sharing = new List<Kind>();
                        }

                        sharing.Add(kind);
                    }
                }

                return byGraphic;
            }

            private static IEnumerable<string> GetSortedNames(IEnumerable<Kind> kinds)
            {
                return kinds.Select(k => k.Name).OrderBy(n => n, StringComparer.Ordinal);
            }

            private Kind GetGroup(string name, string source)
            {
                Kind kind;

                if (!_Groups.TryGetValue(name, out kind))
                {
                    kind = new Kind { Id = _Kinds.Count, Label = name, Name = name, Source = source, Members = new HashSet<Kind>() };
                    _Kinds.Add(kind);
                    _Groups[name] = kind;
                }

                return kind;
            }

            private void AddMember(Kind group, Type type)
            {
                if (type == null || type.IsAbstract || !typeof(Item).IsAssignableFrom(type))
                {
                    return;
                }

                var kind = GetKind(type, null);

                if (kind != null)
                {
                    group.Members.Add(kind);
                }
            }

            // The kind of an item of the type under the label a player reads, or null when no item
            // of the type can be built.
            private Kind GetKind(Type type, string label)
            {
                var facts = GetFacts(type);

                if (facts == null)
                {
                    return null;
                }

                var name = ToName(String.IsNullOrEmpty(label) ? facts.Label : label);

                if (name.Length == 0)
                {
                    name = GetClassName(type);
                }

                return AddKind(type, name, type.Name, facts);
            }

            private Kind AddKind(Type type, string name, string description, Facts facts)
            {
                var source = type.FullName + " " + name;

                Kind kind;

                if (_KindsBySource.TryGetValue(source, out kind))
                {
                    return kind;
                }

                kind = new Kind { Id = _Kinds.Count, Label = name, Source = description, Type = type, Weight = facts.Weight };
                kind.Graphics.UnionWith(facts.Graphics);
                kind.Hues.UnionWith(facts.Hues);
                kind.Names.UnionWith(facts.Names);

                _KindsBySource[source] = kind;
                _Kinds.Add(kind);

                return kind;
            }

            // A recipe that takes a beverage takes it by content, as CraftItem.RequiredBeverage
            // does, and so does a vendor's BeverageBuyInfo. The kind is the drink, in any vessel.
            private Kind GetBeverageKind(BeverageType content)
            {
                Facts facts;

                if (!_BeverageFacts.TryGetValue(content, out facts))
                {
                    facts = GetBeverageFacts(content);
                    _BeverageFacts[content] = facts;
                }

                return AddKind(typeof(BaseBeverage), ToName(SplitWords(content.ToString())), "BaseBeverage holding " + content, facts);
            }

            private Facts GetBeverageFacts(BeverageType content)
            {
                var facts = new Facts();

                foreach (var type in _AllTypes.Where(t => typeof(BaseBeverage).IsAssignableFrom(t) && t.GetConstructor(new[] { typeof(BeverageType) }) != null))
                {
                    BaseBeverage beverage;

                    try
                    {
                        beverage = (BaseBeverage)Activator.CreateInstance(type, content);
                    }
                    catch
                    {
                        continue;
                    }

                    if (!beverage.Deleted && !beverage.IsEmpty && beverage.ComputeItemID() != 0)
                    {
                        facts.Graphics.UnionWith(GetBeverageGraphics(beverage));
                        facts.Hues.Add(beverage.Hue);
                        facts.Names.UnionWith(GetNames(beverage));
                        facts.Weight = Math.Max(facts.Weight, ToTenths(beverage.Weight));
                    }

                    beverage.Delete();
                }

                return facts;
            }

            // A beverage computes its graphic from its content and whether it's empty, and keeps a
            // graphic it's given only where ComputeItemID allows, as a pitcher of water keeps 0x0FF8.
            private static List<int> GetBeverageGraphics(BaseBeverage beverage)
            {
                var original = beverage.ItemID;
                var graphics = new List<int>();

                for (var graphic = 1; graphic <= TileData.MaxItemValue; graphic++)
                {
                    beverage.ItemID = graphic;

                    if (beverage.ComputeItemID() == graphic)
                    {
                        graphics.Add(graphic);
                    }
                }

                beverage.ItemID = original;

                return graphics;
            }

            // The first line of an item's property list is the name line Hob reads. ServUO puts the
            // label there, or for a stack, a material or a mug of something a cliloc of its own
            // with the label as a "#" argument (Item.AddNameProperty, BaseBeverage.AddNameProperty).
            // A name given as text has no cliloc to match.
            private static List<int> GetNames(Item item)
            {
                var names = new List<int>();

                try
                {
                    item.ClearProperties();

                    var list = item.PropertyList;

                    foreach (Match match in Regex.Matches(list.HeaderArgs ?? "", "#([0-9]+)"))
                    {
                        names.Add(Int32.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture));
                    }

                    if (names.Count == 0 && list.Header != 0 && Array.IndexOf(_WrapperClilocs, list.Header) < 0)
                    {
                        names.Add(list.Header);
                    }
                }
                catch
                {
                    names.Clear();
                }

                return names;
            }

            // An abstract type, such as BaseBeverage, stands for each of its concrete types.
            private Facts GetFacts(Type type)
            {
                Facts facts;

                if (_Facts.TryGetValue(type, out facts))
                {
                    return facts;
                }

                if (type.IsAbstract || type.IsInterface)
                {
                    foreach (var concrete in _AllTypes.Where(t => type.IsAssignableFrom(t)))
                    {
                        var part = GetFacts(concrete);

                        if (part != null)
                        {
                            facts = facts ?? new Facts();
                            facts.Graphics.UnionWith(part.Graphics);
                            facts.Hues.UnionWith(part.Hues);
                            facts.Names.UnionWith(part.Names);
                            facts.Weight = Math.Max(facts.Weight, part.Weight);
                        }
                    }
                }
                else
                {
                    facts = Sample(type);
                }

                if (facts == null)
                {
                    _Problems.Add(String.Format("No item of {0} could be built, so it has no kind.", type.Name));
                }

                _Facts[type] = facts;

                return facts;
            }

            private Facts Sample(Type type)
            {
                Facts facts = null;
                var builds = FirstBuilds;

                for (var i = 0; i < builds; i++)
                {
                    var item = Build(type) as Item;

                    if (item == null)
                    {
                        return facts;
                    }

                    if (facts == null)
                    {
                        facts = new Facts { Label = GetLabel(item) };
                    }

                    facts.Graphics.Add(item.ItemID);
                    facts.Hues.Add(item.Hue);
                    facts.Names.UnionWith(GetNames(item));
                    facts.Weight = Math.Max(facts.Weight, ToTenths(item.Weight));

                    if (i == 0 && item is BaseBeverage)
                    {
                        facts.Graphics.UnionWith(GetBeverageGraphics((BaseBeverage)item));
                    }

                    item.Delete();

                    if (i == FirstBuilds - 1 && (facts.Graphics.Count > 1 || facts.Hues.Count > 1))
                    {
                        builds = VariedBuilds;
                    }
                }

                AddFlipped(type, facts.Graphics);
                AddNamesByGraphic(type, facts);

                return facts;
            }

            // An item's label usually follows its graphic (Item.LabelNumber), so a flipped pickaxe
            // reads differently from the one built.
            private static void AddNamesByGraphic(Type type, Facts facts)
            {
                var item = Build(type) as Item;

                if (item == null)
                {
                    return;
                }

                foreach (var graphic in facts.Graphics)
                {
                    try
                    {
                        item.ItemID = graphic;
                    }
                    catch
                    {
                        continue;
                    }

                    facts.Names.UnionWith(GetNames(item));
                }

                item.Delete();
            }

            private string GetLabel(Item item)
            {
                if (!String.IsNullOrEmpty(item.Name))
                {
                    return item.Name;
                }

                return _Clilocs.Get(item.LabelNumber);
            }

            private static object Build(Type type)
            {
                if (type.IsAbstract || type.GetConstructor(Type.EmptyTypes) == null)
                {
                    return null;
                }

                try
                {
                    return Activator.CreateInstance(type);
                }
                catch
                {
                    return null;
                }
            }

            private static void AddFlipped(Type type, SortedSet<int> graphics)
            {
                foreach (FlipableAttribute flipable in type.GetCustomAttributes(typeof(FlipableAttribute), true))
                {
                    if (flipable.ItemIDs != null)
                    {
                        graphics.UnionWith(flipable.ItemIDs);
                    }
                }
            }

            // Every class an exported type derives from, short of Item, holds the kinds of its
            // types once it holds two: any-log for BaseLog, any-shoes for BaseShoes.
            private void AddFamilies()
            {
                var families = new Dictionary<Type, List<Kind>>();

                foreach (var kind in _Kinds.Where(k => !k.IsGroup).ToList())
                {
                    for (var parent = kind.Type; parent != null && parent != typeof(Item); parent = parent.BaseType)
                    {
                        List<Kind> members;

                        if (!families.TryGetValue(parent, out members))
                        {
                            families[parent] = members = new List<Kind>();
                        }

                        members.Add(kind);
                    }
                }

                foreach (var family in families.OrderBy(f => f.Key.Name, StringComparer.Ordinal))
                {
                    if (family.Value.Select(k => k.Type).Distinct().Count() < 2)
                    {
                        continue;
                    }

                    var className = family.Key.Name.StartsWith("Base") && family.Key.Name.Length > 4 ? family.Key.Name.Substring(4) : family.Key.Name;
                    var name = "any-" + ToName(SplitWords(className));

                    if (_Groups.ContainsKey(name))
                    {
                        name = "any-" + GetClassName(family.Key);
                    }

                    var group = GetGroup(name, "Every kind whose type derives from " + family.Key.Name);

                    group.Members.UnionWith(family.Value);
                }
            }

            #endregion

            #region Tools

            private void AddCraftTools()
            {
                foreach (var type in _AllTypes.Where(t => typeof(ITool).IsAssignableFrom(t) && typeof(Item).IsAssignableFrom(t)))
                {
                    var item = Build(type) as Item;

                    if (item == null)
                    {
                        continue;
                    }

                    var system = ((ITool)item).CraftSystem;
                    item.Delete();

                    var info = _Crafts.FirstOrDefault(c => c.System == system);

                    if (info != null)
                    {
                        AddMember(GetGroup(info.Name + "-tools", "Every tool that opens the " + info.Name + " menu"), type);
                    }
                }
            }

            // A harvest tool names its system in a HarvestSystem property, BaseHarvestTool and BaseAxe
            // alike. A fishing pole has none and starts fishing in its OnDoubleClick.
            private void AddHarvestTools()
            {
                foreach (var type in _AllTypes.Where(t => typeof(Item).IsAssignableFrom(t) && StartsHarvesting(t.GetMethod("OnDoubleClick", new[] { typeof(Mobile) }))))
                {
                    HarvestSystem system = null;

                    if (typeof(FishingPole).IsAssignableFrom(type))
                    {
                        system = Fishing.System;
                    }
                    else
                    {
                        var property = type.GetProperties().FirstOrDefault(p => p.Name == "HarvestSystem" && typeof(HarvestSystem).IsAssignableFrom(p.PropertyType));

                        if (property == null)
                        {
                            continue;
                        }

                        var item = Build(type) as Item;

                        if (item == null)
                        {
                            continue;
                        }

                        system = property.GetValue(item) as HarvestSystem;
                        item.Delete();
                    }

                    var info = _Harvests.FirstOrDefault(h => h.System == system);

                    if (info != null)
                    {
                        AddMember(GetGroup(info.Name + "-tools", "Every tool that harvests by " + info.Name), type);
                    }
                }
            }

            private const byte CallOpcode = 0x28;
            private const byte CallVirtOpcode = 0x6F;

            // Hoe derives from BaseAxe, so it has a HarvestSystem, but its OnDoubleClick places a
            // plant and never calls the base. A tool counts only where its OnDoubleClick calls
            // HarvestSystem.BeginHarvesting, itself or through the base OnDoubleClick it calls.
            // The scan reads every call and callvirt token in the method's IL. A byte of an operand
            // that happens to look like one resolves to these two methods by chance only.
            private static bool StartsHarvesting(MethodInfo method)
            {
                var body = method == null ? null : method.GetMethodBody();

                if (body == null)
                {
                    return false;
                }

                var il = body.GetILAsByteArray();

                for (var i = 0; i + 4 < il.Length; i++)
                {
                    if (il[i] != CallOpcode && il[i] != CallVirtOpcode)
                    {
                        continue;
                    }

                    MethodBase called;

                    try
                    {
                        called = method.Module.ResolveMethod(BitConverter.ToInt32(il, i + 1));
                    }
                    catch
                    {
                        continue;
                    }

                    var declaring = called.DeclaringType;

                    if (declaring == null)
                    {
                        continue;
                    }

                    if (called.Name == "BeginHarvesting" && typeof(HarvestSystem).IsAssignableFrom(declaring))
                    {
                        return true;
                    }

                    var isBaseCall = called.Name == "OnDoubleClick" && declaring != method.DeclaringType && declaring.IsAssignableFrom(method.DeclaringType);

                    if (isBaseCall && StartsHarvesting(called as MethodInfo))
                    {
                        return true;
                    }
                }

                return false;
            }

            #endregion

            #region Materials

            private string WriteMaterials()
            {
                var text = new StringBuilder();

                text.AppendLine("# Generated by the craft export of the Celephais server. Export again rather than edit.");
                text.AppendLine("#");
                text.AppendLine("# The material lists of each craft system, first list first. The base is the kind a");
                text.AppendLine("# recipe names, and each material takes its place. The label is that of the material's");
                text.AppendLine("# button, the skill what the main skill must reach to work it, and the hue that of what");
                text.AppendLine("# it makes when the system colours its products. The refusal is the message the server");
                text.AppendLine("# sends when the skill is too low to work the material.");

                foreach (var info in _Crafts)
                {
                    foreach (var list in new[] { info.System.CraftSubRes, info.System.CraftSubRes2 })
                    {
                        if (!list.Init)
                        {
                            continue;
                        }

                        text.AppendLine();
                        text.AppendLine("[[" + info.Name + "]]");
                        text.AppendLine("base = " + QuoteKind(GetKind(list.ResType, null)));
                        text.AppendLine("materials = [");

                        for (var i = 0; i < list.Count; i++)
                        {
                            var material = list.GetAt(i);
                            var kind = GetKind(material.ItemType, null);
                            var resource = CraftResources.GetFromType(material.ItemType);
                            var hue = resource != CraftResource.None ? CraftResources.GetHue(resource) : (kind == null || kind.Hues.Count == 0 ? 0 : kind.Hues.First());

                            text.AppendFormat(
                                "    {{ kind = {0}, label = {1}, skill = {2}, hue = {3}{4} }},",
                                QuoteKind(kind),
                                FormatText(material.NameNumber, material.NameString),
                                FormatSkill(material.RequiredSkill),
                                hue.ToString(CultureInfo.InvariantCulture),
                                FormatMessage(", refusal = ", material.Message));
                            text.AppendLine();
                        }

                        text.AppendLine("]");
                    }
                }

                return text.ToString();
            }

            #endregion

            #region Recipes

            private string WriteRecipes()
            {
                var entries = new List<KeyValuePair<Kind, string>>();
                var labelsByType = new Dictionary<Type, HashSet<string>>();

                foreach (var info in _Crafts)
                {
                    for (var i = 0; i < info.System.CraftItems.Count; i++)
                    {
                        var item = info.System.CraftItems.GetAt(i);
                        HashSet<string> labels;

                        if (!labelsByType.TryGetValue(item.ItemType, out labels))
                        {
                            labelsByType[item.ItemType] = labels = new HashSet<string>();
                        }

                        labels.Add(ToName(ResolveText(item.NameNumber, item.NameString)));
                    }
                }

                foreach (var info in _Crafts)
                {
                    var products = GetGroup(info.Name + "-products", "Everything the " + info.Name + " menu makes");

                    for (var i = 0; i < info.System.CraftItems.Count; i++)
                    {
                        var item = info.System.CraftItems.GetAt(i);
                        var product = GetProductKind(item, labelsByType[item.ItemType].Count > 1);

                        if (product != null)
                        {
                            products.Members.Add(product);
                            entries.Add(new KeyValuePair<Kind, string>(product, WriteRecipe(info, item, product)));
                        }
                    }
                }

                var text = new StringBuilder();

                text.AppendLine("# Generated by the craft export of the Celephais server. Export again rather than edit.");
                text.AppendLine("#");
                text.AppendLine("# Every recipe of every craft system, under the kind it makes. A kind made in more than");
                text.AppendLine("# one way has a recipe for each. The category and the item are the labels of the two");
                text.AppendLine("# buttons of the menu, a cliloc or a text. The material is the list of the system whose");
                text.AppendLine("# base one ingredient is.");

                foreach (var group in entries.GroupBy(e => e.Key))
                {
                    foreach (var entry in group)
                    {
                        text.AppendLine();
                        text.AppendLine("[[" + GetPlaceholder(entry.Key) + "]]");
                        text.Append(entry.Value);
                    }
                }

                return text.ToString();
            }

            // A product is known by its own label, as an agility potion is, and by the label of the
            // menu only where the menu makes one type under several, as GoldRing with each gem.
            private Kind GetProductKind(CraftItem item, bool isNamedByMenu)
            {
                // A product with no constructor to call, such as a CraftableStoneHouseDoor, is known
                // by the graphic the menu shows for it.
                if (!_Facts.ContainsKey(item.ItemType) && item.ItemType.GetConstructor(Type.EmptyTypes) == null)
                {
                    var graphic = item.DisplayID != 0 ? item.DisplayID : CraftItem.ItemIDOf(item.ItemType);

                    if (graphic != 0)
                    {
                        var shown = new Facts();
                        shown.Graphics.Add(graphic);
                        _Facts[item.ItemType] = shown;
                    }
                }

                var facts = GetFacts(item.ItemType);
                var hasOwnLabel = facts != null && !String.IsNullOrEmpty(facts.Label);
                var isLabelledByMenu = isNamedByMenu || !hasOwnLabel;
                var kind = GetKind(item.ItemType, isLabelledByMenu ? ResolveText(item.NameNumber, item.NameString) : null);

                if (kind == null)
                {
                    return null;
                }

                // The built item reads as the plain type, a gold ring without its gem. What the
                // menu makes reads as the menu names it (BaseJewel.LabelNumber).
                if (isLabelledByMenu)
                {
                    kind.Names.Clear();

                    if (item.NameNumber > 0)
                    {
                        kind.Names.Add(item.NameNumber);
                    }
                }

                // An item built with data, such as a CraftableHouseItem, shows its display graphic in
                // the menu, and the bare instance doesn't match it.
                if (item.DisplayID != 0 && item.Data != null)
                {
                    kind.Graphics.Clear();
                    kind.Graphics.Add(item.DisplayID);
                }

                return kind;
            }

            private string WriteRecipe(CraftInfo info, CraftItem item, Kind product)
            {
                var text = new StringBuilder();
                var system = info.System;

                text.AppendLine("tool = " + Quote(info.Name + "-tools"));
                text.AppendLine("category = " + FormatText(item.GroupNameNumber, item.GroupNameString));
                text.AppendLine("item = " + FormatText(item.NameNumber, item.NameString));

                for (var i = 0; i < item.Skills.Count; i++)
                {
                    var skill = item.Skills.GetAt(i);
                    var prefix = i == 0 ? "" : "second-";

                    text.AppendLine(prefix + "skill = " + Quote(ToSkillName(skill.SkillToMake)));
                    text.AppendLine(prefix + "min-skill = " + FormatSkill(skill.MinSkill));
                    text.AppendLine(prefix + "max-skill = " + FormatSkill(skill.MaxSkill));
                }

                var ingredients = new List<string>();

                for (var i = 0; i < item.Resources.Count; i++)
                {
                    var resource = item.Resources.GetAt(i);
                    var label = resource.ItemType.IsAbstract ? ResolveText(resource.NameNumber, resource.NameString) : null;
                    var ingredient = typeof(BaseBeverage).IsAssignableFrom(resource.ItemType) ? GetBeverageKind(item.RequiredBeverage) : GetKind(resource.ItemType, label);

                    ingredients.Add(String.Format(
                        "{{ kind = {0}, amount = {1}{2} }}",
                        QuoteKind(ingredient),
                        resource.Amount.ToString(CultureInfo.InvariantCulture),
                        FormatMessage(", message = ", resource.MessageNumber > 0 ? (object)resource.MessageNumber : resource.MessageString)));
                }

                text.AppendLine("ingredients = [" + String.Join(", ", ingredients) + "]");

                var list = item.UseSubRes2 ? system.CraftSubRes2 : system.CraftSubRes;
                var takesMaterial = false;

                for (var i = 0; i < item.Resources.Count; i++)
                {
                    takesMaterial |= list.Init && item.Resources.GetAt(i).ItemType == list.ResType;
                }

                if (takesMaterial)
                {
                    text.AppendLine("material = " + (item.UseSubRes2 ? "2" : "1"));
                    product.TakesMaterialHue = true;
                }

                var stations = new List<string>();

                if (info.Station != null) stations.Add(info.Station);
                if (item.NeedHeat) stations.Add("heat");
                if (item.NeedOven) stations.Add("oven");
                if (item.NeedMaker) stations.Add("maker");
                if (item.NeedMill) stations.Add("mill");
                if (item.NeedWater) stations.Add("water");

                if (stations.Count > 0)
                {
                    text.AppendLine("stations = [" + String.Join(", ", stations.Select(Quote)) + "]");
                }

                var book = info.Book;

                if (item.RequiresBasketWeaving) book = "basket-weaving";
                if (item.RequiresMechanicalLife) book = "mechanical-life";

                if (book != null)
                {
                    text.AppendLine("book = " + Quote(book));
                }

                if (item.Recipe != null)
                {
                    text.AppendLine("scroll = " + item.Recipe.ID.ToString(CultureInfo.InvariantCulture));
                }

                if (item.UseAllRes)
                {
                    text.AppendLine("use-all = true");
                }

                if (item.RequiresResTarget)
                {
                    text.AppendLine("target = true");
                }

                if (item.Mana > 0)
                {
                    text.AppendLine("mana = " + item.Mana.ToString(CultureInfo.InvariantCulture));
                }

                if (item.RequiredThemePack != ThemePack.None)
                {
                    text.AppendLine("theme-pack = " + Quote(ToName(SplitWords(item.RequiredThemePack.ToString()))));
                }

                return text.ToString();
            }

            #endregion

            #region Harvests

            private string WriteHarvests()
            {
                var text = new StringBuilder();

                text.AppendLine("# Generated by the craft export of the Celephais server. Export again rather than edit.");
                text.AppendLine("#");
                text.AppendLine("# Each harvest of a gathering skill: the tools, what it yields and at what skill, the bonus");
                text.AppendLine("# finds, and for fishing what a catch turns into. A resource's skill is what the harvest");
                text.AppendLine("# needs, and its range is where the skill check gains. The tiles are those a harvest takes,");
                text.AppendLine("# land and statics apart; the special tiles, lava for fishing, take the lava mutations.");
                text.AppendLine("# The range is how far from the tile the character may stand.");

                foreach (var info in _Harvests)
                {
                    var yield = GetGroup(info.Name + "-yield", "Everything " + info.Name + " brings up");

                    foreach (var definition in info.System.Definitions)
                    {
                        text.AppendLine();
                        text.AppendLine("[[" + info.Name + "]]");
                        text.AppendLine("tools = " + Quote(info.Name + "-tools"));
                        text.AppendLine("skill = " + Quote(ToSkillName(definition.Skill)));
                        text.AppendLine("range = " + definition.MaxRange.ToString(CultureInfo.InvariantCulture));
                        WriteTiles(text, "", GetTiles(definition.Tiles, definition.RangedTiles));
                        WriteTiles(text, "special-", GetTiles(definition.SpecialTiles, false));
                        text.AppendLine("resources = [");

                        foreach (var resource in definition.Resources ?? new HarvestResource[0])
                        {
                            text.AppendFormat(
                                "    {{ kinds = [{0}], skill = {1}, min-skill = {2}, max-skill = {3} }},",
                                QuoteKinds(yield, resource.Types),
                                FormatSkill(resource.ReqSkill),
                                FormatSkill(resource.MinSkill),
                                FormatSkill(resource.MaxSkill));
                            text.AppendLine();
                        }

                        text.AppendLine("]");

                        var bonuses = (definition.BonusResources ?? new BonusHarvestResource[0]).Where(b => b.Type != null).ToList();

                        if (bonuses.Count > 0)
                        {
                            text.AppendLine("bonuses = [");

                            foreach (var bonus in bonuses)
                            {
                                text.AppendFormat(
                                    "    {{ kinds = [{0}], skill = {1}, chance = {2} }},",
                                    QuoteKinds(yield, new[] { bonus.Type }),
                                    FormatSkill(bonus.ReqSkill),
                                    FormatSkill(bonus.Chance));
                                text.AppendLine();
                            }

                            text.AppendLine("]");
                        }

                        if (info.System == Fishing.System)
                        {
                            WriteMutations(text, yield);
                        }
                    }

                    if (info.System == Fishing.System)
                    {
                        AddHighSeasFish(yield);
                    }
                }

                return text.ToString();
            }

            // Fishing.MutateType turns a catch into another item by the table m_MutateTable, which is
            // private, as is its MutateEntry. The lava table answers on lava tiles only.
            private void WriteMutations(StringBuilder text, Kind yield)
            {
                var table = typeof(Fishing).GetField("m_MutateTable", BindingFlags.Static | BindingFlags.NonPublic);
                var lavaTable = typeof(Fishing).GetField("m_LavaMutateTable", BindingFlags.Static | BindingFlags.NonPublic);

                foreach (var field in new[] { table, lavaTable })
                {
                    var entries = field == null ? null : field.GetValue(null) as IEnumerable;

                    if (entries == null)
                    {
                        continue;
                    }

                    text.AppendLine((field == table ? "mutations" : "lava-mutations") + " = [");

                    foreach (var entry in entries)
                    {
                        var entryType = entry.GetType();
                        var types = ((Type[])entryType.GetField("m_Types").GetValue(entry)).Where(t => t != null && !t.IsAbstract).ToArray();

                        if (types.Length == 0)
                        {
                            continue;
                        }

                        text.AppendFormat(
                            "    {{ kinds = [{0}], skill = {1}, deep-water = {2} }},",
                            QuoteKinds(yield, types),
                            FormatSkill((double)entryType.GetField("m_ReqSkill").GetValue(entry)),
                            (bool)entryType.GetField("m_DeepWater").GetValue(entry) ? "true" : "false");
                        text.AppendLine();
                    }

                    text.AppendLine("]");
                }
            }

            // FishInfo.GetSpecialItem lands the fish of High Seas: the rare fish of FishInfos, and from
            // Fishing 50 those of the shore, the deep water and the dungeons. A lobster trap brings up
            // the crabs and lobsters.
            private void AddHighSeasFish(Kind yield)
            {
                foreach (var info in FishInfo.FishInfos)
                {
                    var location = info.Location as string;

                    if (location == null || location.ToLowerInvariant() != "cannotfishup")
                    {
                        AddMember(yield, info.Type);
                    }
                }

                foreach (var type in BaseHighseasFish.ShoreFish.Concat(BaseHighseasFish.DeepWaterFish).Concat(BaseHighseasFish.DungeonFish).Concat(BaseHighseasFish.LobstersAndCrabs))
                {
                    AddMember(yield, type);
                }
            }

            // HarvestDefinition.Validate reads Tiles as pairs of bounds when RangedTiles is set. A
            // tile of a static is its graphic plus 0x4000, and anything lower is land.
            private static List<int> GetTiles(int[] tiles, bool isRanged)
            {
                var expanded = new List<int>();

                if (tiles == null)
                {
                    return expanded;
                }

                if (!isRanged)
                {
                    expanded.AddRange(tiles);
                    return expanded;
                }

                for (var i = 0; i + 1 < tiles.Length; i += 2)
                {
                    for (var tile = tiles[i]; tile <= tiles[i + 1]; tile++)
                    {
                        expanded.Add(tile);
                    }
                }

                return expanded;
            }

            private static void WriteTiles(StringBuilder text, string prefix, List<int> tiles)
            {
                const int StaticOffset = 0x4000;

                var land = tiles.Where(t => t < StaticOffset).Distinct().OrderBy(t => t).ToList();
                var statics = tiles.Where(t => t >= StaticOffset).Select(t => t - StaticOffset).Distinct().OrderBy(t => t).ToList();

                if (land.Count > 0)
                {
                    text.AppendLine(prefix + "land = [" + FormatGraphics(land) + "]");
                }

                if (statics.Count > 0)
                {
                    text.AppendLine(prefix + "statics = [" + FormatGraphics(statics) + "]");
                }
            }

            private string QuoteKinds(Kind group, IEnumerable<Type> types)
            {
                var names = new List<string>();

                foreach (var type in types.Where(t => t != null && !t.IsAbstract))
                {
                    var kind = GetKind(type, null);

                    if (kind != null)
                    {
                        group.Members.Add(kind);
                        names.Add(QuoteKind(kind));
                    }
                }

                return String.Join(", ", names);
            }

            #endregion

            #region Stations

            // DefBlacksmithy.CheckAnvilAndForge knows an anvil and a forge by these graphics, and by
            // the Anvil and Forge attributes of a type. The world items it checks take two more
            // forge graphics than the statics do.
            private static readonly int[] _AnvilGraphics = { 4015, 4016, 0x2DD5, 0x2DD6 };
            private static readonly int[] _ForgeGraphics = { 4017, 0x2DD8, 0xA531, 0xA535 };
            private const int LargeForgeFirst = 6522;
            private const int LargeForgeLast = 6569;

            // CraftItem.FindWater counts the components of these addons as water too.
            private static readonly Type[] _WaterAddons = { typeof(KoiPondAddon), typeof(DragonTurtleFountainAddon), typeof(WaterWheelAddon) };

            private string WriteStations()
            {
                var text = new StringBuilder();

                text.AppendLine("# Generated by the craft export of the Celephais server. Export again rather than edit.");
                text.AppendLine("#");
                text.AppendLine("# The graphics of what a recipe's stations name, items and statics alike. A craft needs");
                text.AppendLine("# one within two tiles. A recipe's anvil-and-forge needs an anvil and a forge both.");

                var anvil = new SortedSet<int>(_AnvilGraphics);
                var forge = new SortedSet<int>(_ForgeGraphics);

                for (var graphic = LargeForgeFirst; graphic <= LargeForgeLast; graphic++)
                {
                    forge.Add(graphic);
                }

                foreach (var type in _AllTypes)
                {
                    var isAnvil = type.IsDefined(typeof(AnvilAttribute), false);
                    var isForge = type.IsDefined(typeof(ForgeAttribute), false);

                    if (!isAnvil && !isForge)
                    {
                        continue;
                    }

                    var item = Build(type) as Item;

                    if (item == null)
                    {
                        continue;
                    }

                    (isAnvil ? anvil : forge).Add(item.ItemID);
                    AddFlipped(type, isAnvil ? anvil : forge);
                    item.Delete();
                }

                var water = GetRanges("m_WaterSources");

                foreach (var type in _WaterAddons)
                {
                    var addon = Build(type) as BaseAddon;

                    if (addon == null)
                    {
                        continue;
                    }

                    foreach (var component in addon.Components)
                    {
                        water.Add(component.ItemID);
                    }

                    addon.Delete();
                }

                WriteStation(text, "anvil", anvil);
                WriteStation(text, "forge", forge);
                WriteStation(text, "heat", GetRanges("m_HeatSources"));
                WriteStation(text, "oven", GetRanges("m_Ovens"));
                WriteStation(text, "maker", GetRanges("m_Makers"));
                WriteStation(text, "mill", GetRanges("m_Mills"));
                WriteStation(text, "water", water);

                return text.ToString();
            }

            // CraftItem.Find reads every table as pairs of bounds, the mills too, whose pairs 0x1924
            // to 0x1295 and 0x192E to 0x129F hold nothing.
            private static SortedSet<int> GetRanges(string field)
            {
                var graphics = new SortedSet<int>();
                var table = (int[])typeof(CraftItem).GetField(field, BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);

                for (var i = 0; i + 1 < table.Length; i += 2)
                {
                    for (var graphic = table[i]; graphic <= table[i + 1]; graphic++)
                    {
                        graphics.Add(graphic);
                    }
                }

                return graphics;
            }

            private static void WriteStation(StringBuilder text, string name, SortedSet<int> graphics)
            {
                text.AppendLine();
                text.AppendLine("[" + name + "]");
                text.AppendLine("graphics = [" + FormatGraphics(graphics) + "]");
            }

            #endregion

            #region Vendors

            private List<VendorInfo> GetVendors()
            {
                var shops = typeof(BaseVendor).GetProperty("SBInfos", BindingFlags.Instance | BindingFlags.NonPublic);
                var vendors = new List<VendorInfo>();

                foreach (var type in _AllTypes.Where(t => typeof(BaseVendor).IsAssignableFrom(t)).OrderBy(t => t.Name, StringComparer.Ordinal))
                {
                    var info = GetVendor(type, shops);

                    if (info != null && (info.Sells.Count > 0 || info.Buys.Count > 0))
                    {
                        vendors.Add(info);
                    }
                }

                return vendors;
            }

            // A vendor may pick its shops at random, as Armorer.InitSBInfo picks one of four, so it
            // is built as often as an item that varies. A quest giver or a gypsy banker sets
            // IsActiveVendor false and never trades.
            private VendorInfo GetVendor(Type type, PropertyInfo shops)
            {
                VendorInfo info = null;
                var builds = FirstBuilds;
                var isVaried = false;

                for (var i = 0; i < builds; i++)
                {
                    var vendor = Build(type) as BaseVendor;

                    if (vendor == null || !vendor.IsActiveVendor)
                    {
                        if (vendor != null)
                        {
                            vendor.Delete();
                        }

                        return info;
                    }

                    if (info == null)
                    {
                        info = new VendorInfo { Name = GetClassName(type), Title = vendor.Title };
                    }

                    var known = info.Sells.Count + info.Buys.Count;
                    var infos = shops.GetValue(vendor) as List<SBInfo>;

                    if (infos != null)
                    {
                        AddShops(info, infos, vendor.IsActiveSeller, vendor.IsActiveBuyer);
                    }

                    vendor.Delete();

                    isVaried |= i > 0 && info.Sells.Count + info.Buys.Count > known;

                    if (i == FirstBuilds - 1 && isVaried)
                    {
                        builds = VariedBuilds;
                    }
                }

                return info;
            }

            private void AddShops(VendorInfo info, List<SBInfo> infos, bool isSelling, bool isBuying)
            {
                foreach (var shop in infos)
                {
                    if (isSelling)
                    {
                        foreach (var good in shop.BuyInfo)
                        {
                            AddOffer(info.Sells, good);
                        }
                    }

                    if (isBuying && shop.SellInfo != null)
                    {
                        foreach (var good in shop.SellInfo.Types)
                        {
                            AddGood(info.Buys, good);
                        }
                    }
                }
            }

            private static string WriteVendors(List<VendorInfo> vendors)
            {
                var text = new StringBuilder();

                text.AppendLine("# Generated by the craft export of the Celephais server. Export again rather than edit.");
                text.AppendLine("#");
                text.AppendLine("# What each type of vendor sells and buys, by kind. The title is what the vendor wears");
                text.AppendLine("# after its name. Prices aren't here: they drift with trade, and the shop window shows them.");

                foreach (var vendor in vendors)
                {
                    text.AppendLine();
                    text.AppendLine("[" + vendor.Name + "]");

                    if (!String.IsNullOrEmpty(vendor.Title))
                    {
                        text.AppendLine("title = " + Quote(vendor.Title));
                    }

                    text.AppendLine("sells = [" + String.Join(", ", GetSortedNames(vendor.Sells).Select(Quote)) + "]");
                    text.AppendLine("buys = [" + String.Join(", ", GetSortedNames(vendor.Buys).Select(Quote)) + "]");
                }

                return text.ToString();
            }

            // A vendor shows a good with the graphic and the name of its buy info, and these needn't
            // be the item's own: 1020000 plus the graphic unless the buy info names it
            // (GenericBuyInfo). A BeverageBuyInfo fills its vessel with a drink.
            private void AddOffer(HashSet<Kind> goods, GenericBuyInfo good)
            {
                var type = good.Type;

                if (type == null || type.IsAbstract || !typeof(Item).IsAssignableFrom(type))
                {
                    return;
                }

                IEntity shown = null;

                try
                {
                    shown = good.GetEntity();
                }
                catch
                {
                }

                var beverage = shown as BaseBeverage;
                var kind = beverage != null && !beverage.IsEmpty ? GetBeverageKind(beverage.Content) : GetKind(type, null);

                if (shown != null)
                {
                    shown.Delete();
                }

                if (kind == null)
                {
                    return;
                }

                if (!kind.Graphics.Contains(good.ItemID))
                {
                    kind.ShopGraphics.Add(good.ItemID);
                }

                int name;

                if (Int32.TryParse(good.Name, NumberStyles.None, CultureInfo.InvariantCulture, out name))
                {
                    kind.Names.Add(name);
                }

                goods.Add(kind);
            }

            // A vendor also sells pack animals, which are mobiles and have no kind.
            private void AddGood(HashSet<Kind> goods, Type type)
            {
                if (type == null || type.IsAbstract || !typeof(Item).IsAssignableFrom(type))
                {
                    return;
                }

                var kind = GetKind(type, null);

                if (kind != null)
                {
                    goods.Add(kind);
                }
            }

            #endregion

            #region Text

            private string ResolveText(int number, string text)
            {
                return number > 0 ? _Clilocs.Get(number) : text;
            }

            private static string FormatText(int number, string text)
            {
                return number > 0 ? number.ToString(CultureInfo.InvariantCulture) : Quote(text ?? "");
            }

            private static string FormatGraphics(IEnumerable<int> graphics)
            {
                return String.Join(", ", graphics.Select(g => "0x" + g.ToString("X4")));
            }

            // A message is a cliloc or a text, and nothing when the server sends none of its own.
            private static string FormatMessage(string prefix, object message)
            {
                if (message is TextDefinition)
                {
                    var definition = (TextDefinition)message;
                    message = definition.Number > 0 ? (object)definition.Number : definition.String;
                }

                if (message is int && (int)message > 0)
                {
                    return prefix + ((int)message).ToString(CultureInfo.InvariantCulture);
                }

                var text = message as string;

                return String.IsNullOrEmpty(text) ? "" : prefix + Quote(text);
            }

            private static string FormatSkill(double skill)
            {
                return skill.ToString("0.0##", CultureInfo.InvariantCulture);
            }

            // Hob names skills as the skill window does, in kebab case, and Bowcraft/Fletching by its
            // first word.
            private static string ToSkillName(SkillName skill)
            {
                var name = SkillInfo.Table[(int)skill].Name;
                var slash = name.IndexOf('/');

                return ToName(slash < 0 ? name : name.Substring(0, slash));
            }

            // A cliloc of an item's graphic carries the plural in markers, as in "loa%ves/f%" and
            // "ingot%s%", and an argument as "~1_AMT~".
            private static string ToName(string label)
            {
                if (label == null)
                {
                    return "";
                }

                var text = Regex.Replace(label, "%[^%/]*/([^%]*)%", "$1");
                text = Regex.Replace(text, "%[^%]*%", "");
                text = Regex.Replace(text, "~[^~]*~", "");
                text = Regex.Replace(text, "<[^>]*>", "");
                text = text.Replace("'", "").ToLowerInvariant();
                text = Regex.Replace(text, "[^a-z0-9]+", "-");
                text = Regex.Replace(text, "^-*(a|an)-", "");

                return text.Trim('-');
            }

            private static string SplitWords(string name)
            {
                return Regex.Replace(name, "(?<=[a-z0-9])(?=[A-Z])", " ");
            }

            private static string Quote(string text)
            {
                var quoted = new StringBuilder("\"");

                foreach (var c in text)
                {
                    if (c == '"' || c == '\\')
                    {
                        quoted.Append('\\').Append(c);
                    }
                    else if (c < 0x20)
                    {
                        quoted.AppendFormat("\\u{0:X4}", (int)c);
                    }
                    else
                    {
                        quoted.Append(c);
                    }
                }

                return quoted.Append('"').ToString();
            }

            private static int ToTenths(double weight)
            {
                return (int)Math.Round(weight * 10.0);
            }

            #endregion

            private static List<Type> GetAllTypes()
            {
                var types = new List<Type>();

                foreach (var assembly in ScriptCompiler.Assemblies.Concat(new[] { Core.Assembly }).Distinct())
                {
                    types.AddRange(assembly.GetTypes().Where(t => t.IsClass && !t.IsAbstract && !t.ContainsGenericParameters));
                }

                return types;
            }
        }

        #region Clilocs

        private sealed class Clilocs
        {
            // The fourth header byte a newer client sets on a compressed Cliloc.enu. Ultima.StringList
            // reads only the uncompressed form, so it finds nothing in the files of 7.0.116.
            private const byte CompressedMarker = 0x8E;

            private readonly Dictionary<int, string> _Entries = new Dictionary<int, string>();

            public static Clilocs Load()
            {
                var path = Core.FindDataFile("Cliloc.enu") ?? Core.FindDataFile("cliloc.enu");

                if (path == null)
                {
                    throw new FileNotFoundException("Cliloc.enu isn't in any data folder.");
                }

                var raw = File.ReadAllBytes(path);

                if (raw.Length >= 4 && raw[3] == CompressedMarker)
                {
                    raw = Bwt.Decompress(raw);
                }

                var clilocs = new Clilocs();

                for (var position = 6; position + 7 <= raw.Length;)
                {
                    var number = BitConverter.ToInt32(raw, position);
                    var length = BitConverter.ToUInt16(raw, position + 5);

                    position += 7;

                    if (position + length > raw.Length)
                    {
                        throw new InvalidDataException(String.Format("Cliloc {0} runs past the end of the file.", number));
                    }

                    clilocs._Entries[number] = Encoding.UTF8.GetString(raw, position, length).Trim();
                    position += length;
                }

                return clilocs;
            }

            public string Get(int number)
            {
                string text;

                return _Entries.TryGetValue(number, out text) ? text : null;
            }
        }

        // Ported from the Go reader of Dreaming-UO, itself from ClassicUO's BwtDecompress: a
        // move-to-front pass over the bytes after a four-byte header, then an inverse transform
        // driven by a table of 256 symbol counts.
        private static class Bwt
        {
            private const int HeaderLength = 4;
            private const int CountsLength = 256 * 4;
            private const long OutputLimit = 64L << 20;

            public static byte[] Decompress(byte[] buffer)
            {
                if (buffer.Length <= HeaderLength)
                {
                    throw new InvalidDataException("The compressed cliloc file is too short for its header.");
                }

                var order = new byte[256];

                for (var i = 0; i < order.Length; i++)
                {
                    order[i] = (byte)i;
                }

                // The last byte read is never decoded, so the final slot stays zero.
                var symbols = new byte[buffer.Length - HeaderLength];
                var index = buffer[HeaderLength];

                for (var position = HeaderLength + 1; position < buffer.Length; position++)
                {
                    var value = order[index];

                    Array.Copy(order, 0, order, 1, index);
                    order[0] = value;

                    symbols[position - HeaderLength - 1] = value;
                    index = buffer[position];
                }

                return Invert(symbols);
            }

            private static byte[] Invert(byte[] input)
            {
                if (input.Length < CountsLength)
                {
                    throw new InvalidDataException("The compressed cliloc file is too short for its symbol counts.");
                }

                var counts = new int[256];
                var starts = new int[256];
                var ends = new int[256];
                long total = 0;
                var symbolCount = 0;

                for (var i = 0; i < counts.Length; i++)
                {
                    counts[i] = BitConverter.ToInt32(input, i * 4);

                    if (counts[i] < 0)
                    {
                        throw new InvalidDataException("The compressed cliloc file has a negative symbol count.");
                    }

                    if (counts[i] != 0)
                    {
                        symbolCount++;
                    }

                    total += counts[i];
                }

                if (total > OutputLimit)
                {
                    throw new InvalidDataException("The compressed cliloc file claims an impossible size.");
                }

                var table = new byte[256];

                for (var i = 0; i < table.Length; i++)
                {
                    table[i] = (byte)i;
                }

                var frequency = GetSymbolsByCount(counts);

                for (int i = 0, m = 0; i < symbolCount; i++)
                {
                    var symbol = frequency[i];

                    if (m + CountsLength >= input.Length)
                    {
                        throw new InvalidDataException("The compressed cliloc file ends inside its symbol table.");
                    }

                    table[input[m + CountsLength]] = symbol;
                    starts[symbol] = m + 1;
                    m += counts[symbol];
                    ends[symbol] = m;
                }

                var output = new byte[total];
                var current = table[0];

                for (var i = 0; i < output.Length; i++)
                {
                    output[i] = current;

                    if (starts[current] >= ends[current])
                    {
                        if (symbolCount > 0)
                        {
                            symbolCount--;
                            Array.Copy(table, 1, table, 0, symbolCount);
                            current = table[0];
                        }

                        continue;
                    }

                    var position = starts[current] + CountsLength;

                    if (position >= input.Length)
                    {
                        throw new InvalidDataException("The compressed cliloc file ends before its data does.");
                    }

                    var shift = input[position];
                    starts[current]++;

                    if (shift != 0)
                    {
                        Array.Copy(table, 1, table, 0, shift);
                        table[shift] = current;
                        current = table[0];
                    }
                }

                return output;
            }

            // Most frequent first. A tie goes to the lower symbol.
            private static byte[] GetSymbolsByCount(int[] counts)
            {
                var ordered = new byte[256];
                var remaining = (int[])counts.Clone();

                for (var i = 0; i < ordered.Length; i++)
                {
                    int best = 0, bestCount = 0;

                    for (var symbol = 0; symbol < remaining.Length; symbol++)
                    {
                        if (remaining[symbol] > bestCount)
                        {
                            best = symbol;
                            bestCount = remaining[symbol];
                        }
                    }

                    if (bestCount == 0)
                    {
                        break;
                    }

                    ordered[i] = (byte)best;
                    remaining[best] = 0;
                }

                return ordered;
            }
        }

        #endregion
    }
}
