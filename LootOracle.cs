using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Numerics;
using ExileCore2;
using ExileCore2.PoEMemory.MemoryObjects;
using ExileCore2.Shared.Cache;
using ExileCore2.Shared.Enums;
using ImGuiNET;
using ItemFilterLibrary;

namespace LootOracle;

public class LootOracle : BaseSettingsPlugin<LootOracleSettings>
{
    private CachedValue<List<ServerInventory.InventSlotItem>> _inventoryCache;
    private ProfileManager _profileManager;

    // Bump when DefaultRules() changes — forces overwrite of stale saved rules on load.
    private const int CurrentRulesVersion = 36;

    public override bool Initialise()
    {
        Name = "LootOracle";
        EnsureDefaultRules();
        _inventoryCache = new TimeCache<List<ServerInventory.InventSlotItem>>(GetInventoryItems, 200);
        _profileManager = new ProfileManager(ConfigDirectory);
        return true;
    }

    private static bool IsOffensiveSlot(string className)
    {
        string cls = (className ?? "").ToLowerInvariant();
        return cls.Contains("bow") || cls.Contains("crossbow") ||
               cls.Contains("quiver") ||
               cls.Contains("gloves") || cls.Contains("gauntlets") || cls.Contains("bracers") || cls.Contains("mitts") ||
               cls.Contains("ring") || cls.Contains("amulet") || cls.Contains("talisman") ||
               cls.Contains("staff") || cls.Contains("spear") || cls.Contains("mace") ||
               cls.Contains("quarterstaff") || cls.Contains("sword") || cls.Contains("axe") ||
               cls.Contains("wand") || cls.Contains("sceptre");
    }

    private static bool ModMatchesComboKeyword(string r, string n, List<string> comboMods)
    {
        if (comboMods == null || comboMods.Count == 0) return false;
        foreach (var kw in comboMods)
            if (r.Contains(kw) || n.Contains(kw)) return true;
        return false;
    }

    private void EnsureDefaultRules()
    {
        if (Settings.Rules.Count == 0 || Settings.RulesVersion < CurrentRulesVersion)
        {
            Settings.Rules = DefaultRules();
            Settings.RulesVersion = CurrentRulesVersion;
        }
    }

    private static List<FilterRule> DefaultRules()
    {
        return new List<FilterRule>
        {
            new()
            {
                Name = "GOD Tier",
                Enabled = true,
                Color = new SerializableColor { R = 255, G = 20, B = 147, A = 255 } // DeepPink
            },
            new()
            {
                Name = "BiS",
                Enabled = true,
                Color = new SerializableColor { R = 50, G = 205, B = 50, A = 255 } // LimeGreen
            },
            new()
            {
                Name = "Trade",
                Enabled = true,
                Color = new SerializableColor { R = 255, G = 215, B = 0, A = 255 } // Gold
            },
            new()
            {
                Name = "Craft Ciano",
                Enabled = true,
                Color = new SerializableColor { R = 0, G = 255, B = 255, A = 255 } // Cyan
            },
            new()
            {
                Name = "Craft Base",
                Enabled = true,
                Color = new SerializableColor { R = 30, G = 144, B = 255, A = 255 } // DodgerBlue
            },
        };
    }

    private static int ParseTier(string rawName)
    {
        if (string.IsNullOrEmpty(rawName)) return 1;

        // Find the last sequence of digits in the string
        int i = rawName.Length - 1;
        while (i >= 0 && !char.IsDigit(rawName[i]))
        {
            i--;
        }

        if (i < 0) return 1; // No digits found, assume single-tier mod (always T1)

        // Read the digits backwards to handle multidigit numbers
        string digitStr = "";
        while (i >= 0 && char.IsDigit(rawName[i]))
        {
            digitStr = rawName[i] + digitStr;
            i--;
        }

        if (int.TryParse(digitStr, out int tier))
        {
            return tier;
        }

        return 1;
    }

    private static bool ContainsAny(string str, params string[] keywords)
    {
        if (string.IsNullOrEmpty(str)) return false;
        foreach (var kw in keywords)
        {
            if (str.Contains(kw, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static bool IsModRelevant(string className, string modName, string rawName)
    {
        if (string.IsNullOrEmpty(className) || string.IsNullOrEmpty(modName)) return false;

        string cls = className.ToLowerInvariant();
        string name = modName.ToLowerInvariant();
        string raw = (rawName ?? "").ToLowerInvariant();

        // STRICT FILTER: Exclude intrinsic base item mods (like local flat base evasion/ES)
        if (raw.Contains("localbase") || name.Contains("localbase") || raw.Contains("baseitem") || name.Contains("baseitem"))
            return false;

        // STRICT FILTER: Exclude flask utility and recovery mods from counting as core Life/Mana stats
        if (raw.Contains("flask") || name.Contains("flask") || raw.Contains("recovery") || name.Contains("recovery"))
            return false;

        // STRICT FILTER: Exclude mods with no build value (thorns, stun threshold, block)
        if (ContainsAny(raw, "thorns", "stun", "block") || ContainsAny(name, "thorns", "stun", "block"))
            return false;

        bool hasPhys = ContainsAny(raw, "phys") || ContainsAny(name, "phys");
        bool hasCold = ContainsAny(raw, "cold") || ContainsAny(name, "cold");
        // "lightning" specifically — avoids "blight", "enlighten", "highlight"
        bool hasLight = raw.Contains("lightning") || name.Contains("lightning");
        // "fire" for damage — FireResist caught by hasResist separately
        bool hasFire = (raw.Contains("fire") || name.Contains("fire")) && !raw.Contains("resist") && !name.Contains("resist");
        bool hasAdded = ContainsAny(raw, "added") || ContainsAny(name, "added");
        bool hasChaos = ContainsAny(raw, "chaos") || ContainsAny(name, "chaos");
        // % increased damage (fire/cold/lightning/phys/chaos) — catches IncreasedFireDamage, IncreasedChaosDamage, etc.
        // Excludes resist mods, damage taken, enemy damage
        bool hasIncreasedElemDmg = (hasCold || hasLight || hasFire || hasPhys || hasChaos) &&
                                    (ContainsAny(raw, "increased", "damage") || ContainsAny(name, "increased", "damage")) &&
                                    !ContainsAny(raw, "resist", "taken", "enemy") && !ContainsAny(name, "resist", "taken", "enemy");
        // Item Rarity — confirmed name: ItemFoundRarityIncrease / ItemFoundRarityIncreasePrefix
        bool hasRarity = ContainsAny(raw, "itemfoundrarity", "rarityfound") || ContainsAny(name, "itemfoundrarity", "rarityfound");
        bool hasCrit = ContainsAny(raw, "critical") || ContainsAny(name, "critical");
        bool hasAttackSpeed = (raw.Contains("attackspeed") || name.Contains("attackspeed") ||
                               (ContainsAny(raw, "attack") && ContainsAny(raw, "speed"))) ||
                              (ContainsAny(name, "attack") && ContainsAny(name, "speed"));
        bool hasCastSpeed = raw.Contains("castspeed") || name.Contains("castspeed") ||
                            (ContainsAny(raw, "cast") && ContainsAny(raw, "speed")) ||
                            (ContainsAny(name, "cast") && ContainsAny(name, "speed"));

        // Life — exclude regen, decay, leech, on-kill/per-kill/on-hit variants
        bool hasLife = (ContainsAny(raw, "life") || ContainsAny(name, "life")) &&
                       !ContainsAny(raw, "regen", "decay", "leech", "kill", "death", "onhit", "gained") &&
                       !ContainsAny(name, "regen", "decay", "leech", "kill", "death", "onhit", "gained");
        // Mana — exclude regen, leech, on-kill/on-hit variants
        bool hasMana = (ContainsAny(raw, "mana") || ContainsAny(name, "mana")) &&
                       !ContainsAny(raw, "regen", "leech", "kill", "death", "gained") &&
                       !ContainsAny(name, "regen", "leech", "kill", "death", "gained");

        bool hasSpirit = ContainsAny(raw, "spirit") || ContainsAny(name, "spirit");

        // Resistances: strict — must contain "resist" or "allelemental"
        bool hasResist = ContainsAny(raw, "resist", "allelemental") || ContainsAny(name, "resist", "allelemental");

        // Energy Shield: must contain both "energy" and "shield" to avoid false positives
        bool hasEnergy = (raw.Contains("energyshield") || name.Contains("energyshield") ||
                          (raw.Contains("energy") && raw.Contains("shield")) ||
                          (name.Contains("energy") && name.Contains("shield")));
        // Armour: exclude evasion hybrids and attribute requirements
        bool hasArmour = (ContainsAny(raw, "armour", "armor") || ContainsAny(name, "armour", "armor")) &&
                         !ContainsAny(raw, "requirement", "evasion") && !ContainsAny(name, "requirement", "evasion");
        bool hasAttr = ContainsAny(raw, "strength", "dexterity", "intelligence", "attribute") ||
                       ContainsAny(name, "strength", "dexterity", "intelligence", "attribute");
        bool hasAilment = ContainsAny(raw, "ailment", "freeze", "shock", "bleed", "poison", "ignite") ||
                          ContainsAny(name, "ailment", "freeze", "shock", "bleed", "poison", "ignite");
        bool hasCharm = ContainsAny(raw, "charm") || ContainsAny(name, "charm");

        // 1. Bow
        if (cls.Contains("bow") && !cls.Contains("crossbow"))
        {
            return (hasAdded && (hasPhys || hasCold || hasLight)) ||
                   hasIncreasedElemDmg ||
                   raw.Contains("physicaldamage") || name.Contains("physicaldamage") ||
                   hasAttackSpeed || hasCrit ||
                   ContainsAny(raw, "project") || ContainsAny(name, "project");
        }

        // 2. Quiver (global mods — no Local prefix)
        if (cls.Contains("quiver"))
        {
            return (hasAdded && (hasPhys || hasCold || hasLight || hasFire)) ||
                   hasCrit || hasAttackSpeed ||
                   ContainsAny(raw, "project") || ContainsAny(name, "project") ||
                   ContainsAny(raw, "bow") || ContainsAny(name, "bow") ||
                   ContainsAny(raw, "pierce") || ContainsAny(name, "pierce") ||
                   hasLife || hasSpirit || hasMana || hasResist || hasAilment;
        }

        // 3. Melee Weapon (Quarterstaff / Spear / Mace / Sceptre)
        if (cls.Contains("staff") || cls.Contains("spear") || cls.Contains("mace") ||
            cls.Contains("quarterstaff") || cls.Contains("sceptre") || cls.Contains("sword") || cls.Contains("axe"))
        {
            return (hasAdded && (hasPhys || hasCold || hasLight || hasFire)) ||
                   raw.Contains("physicaldamage") || name.Contains("physicaldamage") ||
                   ContainsAny(raw, "elemental") || ContainsAny(name, "elemental") ||
                   hasCrit || hasAttackSpeed ||
                   ContainsAny(raw, "melee") || ContainsAny(name, "melee") ||
                   ContainsAny(raw, "spell") || ContainsAny(name, "spell");
        }

        // 4. Wand / Caster off-hand (Foci)
        if (cls.Contains("wand") || cls.Contains("staves") || cls.Contains("foci") || cls.Contains("focus"))
        {
            return ContainsAny(raw, "spell") || ContainsAny(name, "spell") ||
                   hasCastSpeed || hasEnergy || hasCrit || hasMana ||
                   (hasAdded && (hasPhys || hasCold || hasLight));
        }

        // 5. Crossbow
        if (cls.Contains("crossbow"))
        {
            return (hasAdded && (hasPhys || hasCold || hasLight || hasFire)) ||
                   ContainsAny(raw, "elemental") || ContainsAny(name, "elemental") ||
                   hasCrit || hasAttackSpeed ||
                   ContainsAny(raw, "attackskill");
        }

        // 6. Body Armour / Helmet
        if (cls.Contains("body") || cls.Contains("armour") || cls.Contains("helmet") ||
            cls.Contains("tiara") || cls.Contains("circlet") || cls.Contains("crest") || cls.Contains("crown"))
        {
            return hasLife || hasSpirit || hasMana || hasResist ||
                   hasEnergy || hasArmour ||
                   ContainsAny(raw, "evasion") || ContainsAny(name, "evasion");
        }

        // 7. Boots
        if (cls.Contains("boots") || cls.Contains("shoes") || cls.Contains("greaves") || cls.Contains("sandals"))
        {
            return raw.Contains("velocity") || raw.Contains("movement") || name.Contains("movement") ||
                   hasLife || hasMana || hasResist ||
                   hasEnergy || hasArmour ||
                   ContainsAny(raw, "evasion") || ContainsAny(name, "evasion") ||
                   hasAttr;
        }

        // 8. Gloves
        if (cls.Contains("gloves") || cls.Contains("gauntlets") || cls.Contains("bracers") || cls.Contains("mitts"))
        {
            return (hasAdded && (hasPhys || hasCold || hasLight || hasFire)) ||
                   hasCrit || hasAttackSpeed ||
                   hasLife || hasMana || hasResist ||
                   hasEnergy || hasArmour ||
                   ContainsAny(raw, "evasion") || ContainsAny(name, "evasion") ||
                   hasAttr || hasAilment;
        }

        // 9. Rings / Amulets
        if (cls.Contains("ring") || cls.Contains("amulet") || cls.Contains("talisman"))
        {
            bool hasSkillLevel = (raw.Contains("skilllevel") || raw.Contains("skillgem") || raw.Contains("gemlevel") ||
                                  name.Contains("skilllevel") || name.Contains("skillgem") || name.Contains("gemlevel"));
            return (hasAdded && (hasPhys || hasCold || hasLight || hasFire)) ||
                   hasIncreasedElemDmg ||
                   hasSkillLevel || hasRarity ||
                   ContainsAny(raw, "project") || ContainsAny(name, "project") ||
                   ContainsAny(raw, "melee") || ContainsAny(name, "melee") ||
                   ContainsAny(raw, "spell") || ContainsAny(name, "spell") ||
                   hasCrit || hasAttackSpeed || hasCastSpeed ||
                   hasLife || hasSpirit || hasMana || hasResist || hasAttr || hasAilment;
        }

        // 10. Belt
        if (cls.Contains("belt") || cls.Contains("sash"))
        {
            return hasLife || hasMana || hasResist || hasArmour || hasAttr || hasCharm;
        }

        return false;
    }

    private static int GetModScore(string name, string rawName, int digit, string itemClass, out bool isGood)
    {
        string n = name.ToLowerInvariant();
        string r = (rawName ?? "").ToLowerInvariant();
        string cls = (itemClass ?? "").ToLowerInvariant();
        isGood = false;

        // Hard exclude: on-kill, on-death, life/mana gained on hit
        if (ContainsAny(r, "kill", "death", "gained", "onhit") || ContainsAny(n, "kill", "death", "gained", "onhit"))
            return 0;

        // ── GOD TIER ────────────────────────────────────────────────────────────

        // Skill Gem Levels — always GOD regardless of tier
        bool isSkillLevel = (r.Contains("skilllevel") || r.Contains("skillgem") || r.Contains("gemlevel") ||
                             n.Contains("skilllevel") || n.Contains("skillgem") || n.Contains("gemlevel")) &&
                            !r.Contains("localbase") && !n.Contains("localbase");
        if (isSkillLevel) { isGood = true; return 25; }

        // Movement Speed T1-T2 (digit >= 5 = approx T1/T2 in PoE2 scale)
        if (r.Contains("movement") || n.Contains("movement") || r.Contains("velocity") || n.Contains("velocity"))
        {
            if (digit >= 5) { isGood = true; return 25; }
            if (digit == 4) { isGood = true; return 18; }
            if (digit == 3) { isGood = true; return 12; }
            return 5;
        }

        // Critical Multiplier — GOD for all attack/spell builds
        bool isCritMult = (ContainsAny(r, "critical") || ContainsAny(n, "critical")) &&
                          (ContainsAny(r, "multiplier", "multi") || ContainsAny(n, "multiplier", "multi"));
        if (isCritMult)
        {
            if (digit >= 5) { isGood = true; return 18; }
            if (digit >= 3) { isGood = true; return 12; }
            isGood = true; return 7;
        }

        // ArmourApplies to Elemental Damage — GOD for Warrior/Mercenary
        bool isArmourElem = (r.Contains("armourapplies") || n.Contains("armourapplies") ||
                             (ContainsAny(r, "armour", "armor") && r.Contains("elemental")) ||
                             (ContainsAny(n, "armour", "armor") && n.Contains("elemental")));
        if (isArmourElem)
        {
            if (digit >= 5) { isGood = true; return 16; }
            if (digit >= 3) { isGood = true; return 12; }
            isGood = true; return 8;
        }

        // Deflection (EvasionGrantsDeflection, ArmourGrantsDeflection) — GOD defensive multiplier
        if (ContainsAny(r, "deflect", "deflection") || ContainsAny(n, "deflect", "deflection"))
        {
            if (digit >= 5) { isGood = true; return 14; }
            if (digit >= 3) { isGood = true; return 10; }
            isGood = true; return 7;
        }

        // Resistance T1 (digit >= 7 in PoE2 absolute tier scale)
        if (r.Contains("resist") || n.Contains("resist") || r.Contains("resistance") || n.Contains("resistance"))
        {
            if (digit >= 7) { isGood = true; return 14; }  // GOD - T1
            if (digit >= 5) { isGood = true; return 8; }   // GOOD - T2/T3
            if (digit >= 3) { isGood = true; return 5; }   // GOOD - T3/T4 border
            return 3;                                        // AVERAGE - T5+
        }

        // ── GOOD TIER ────────────────────────────────────────────────────────────

        // Spirit
        if (r.Contains("spirit") || n.Contains("spirit"))
        {
            if (digit >= 7) { isGood = true; return 15; }
            if (digit >= 5) { isGood = true; return 10; }
            if (digit >= 3) { isGood = true; return 8; }
            isGood = true; return 6;
        }

        // Maximum Life (excluding regen)
        if ((r.Contains("life") || n.Contains("life")) && !ContainsAny(r, "regene") && !ContainsAny(n, "regene"))
        {
            if (digit >= 9) { isGood = true; return 15; }
            if (digit >= 7) { isGood = true; return 10; }
            if (digit >= 5) { isGood = true; return 7; }
            if (digit >= 3) return 4;
            return 1;
        }

        // Hybrid defence mods (Evasion+Life, Armour+ES, etc.)
        bool isHybridDefence = (ContainsAny(r, "evasion", "armour", "armor", "energyshield") ||
                                ContainsAny(n, "evasion", "armour", "armor", "energyshield")) &&
                               (ContainsAny(r, "life", "shield", "armour", "evasion") ||
                                ContainsAny(n, "life", "shield", "armour", "evasion")) &&
                               r.Length > 10;
        if (isHybridDefence && !ContainsAny(r, "requirement", "localbase") && !ContainsAny(n, "requirement", "localbase"))
        {
            if (digit >= 5) { isGood = true; return 12; }
            if (digit >= 3) { isGood = true; return 8; }
            isGood = true; return 5;
        }

        // Flat Cold Damage -- GOOD (best elemental for Cold builds this patch)
        if ((r.Contains("added") || n.Contains("added")) && (r.Contains("cold") || n.Contains("cold")))
        {
            if (digit >= 5) { isGood = true; return 12; }
            if (digit >= 3) { isGood = true; return 6; }
            return 2;
        }

        // Flat Physical Damage — GOOD
        if ((r.Contains("added") || n.Contains("added")) && (r.Contains("phys") || n.Contains("phys")))
        {
            if (digit >= 5) { isGood = true; return 12; }
            if (digit >= 3) { isGood = true; return 6; }
            return 2;
        }

        // Flat Lightning Damage — GOOD
        if ((r.Contains("added") || n.Contains("added")) && (r.Contains("lightning") || n.Contains("lightning")))
        {
            if (digit >= 5) { isGood = true; return 12; }
            if (digit >= 3) { isGood = true; return 6; }
            return 2;
        }

        // Flat Fire Damage -- GOOD but lower than Cold (fewer Cold builds this patch)
        if ((r.Contains("added") || n.Contains("added")) && (r.Contains("fire") || n.Contains("fire")))
        {
            if (digit >= 5) { isGood = true; return 8; }
            if (digit >= 3) { isGood = true; return 4; }
            return 1;
        }

        // Critical Strike Chance (not multiplier — handled above)
        if (ContainsAny(r, "critical") || ContainsAny(n, "critical"))
        {
            if (digit >= 5) { isGood = true; return 12; }
            if (digit >= 3) { isGood = true; return 5; }
            return 2;
        }

        // Attack Speed / Cast Speed
        bool isAtkSpeed = r.Contains("attackspeed") || (r.Contains("attack") && r.Contains("speed")) ||
                          n.Contains("attackspeed") || (n.Contains("attack") && n.Contains("speed"));
        bool isCastSpeed = r.Contains("castspeed") || (r.Contains("cast") && r.Contains("speed")) ||
                           n.Contains("castspeed") || (n.Contains("cast") && n.Contains("speed"));
        if (isAtkSpeed || isCastSpeed)
        {
            if (digit >= 5) { isGood = true; return 12; }
            if (digit == 4) { isGood = true; return 8; }
            if (digit == 3) return 5;
            return 2;
        }

        // ── AVERAGE TIER ──────────────────────────────────────────────────────────

        // Item Rarity
        if (ContainsAny(r, "itemfoundrarity", "rarityfound") || ContainsAny(n, "itemfoundrarity", "rarityfound"))
        {
            if (digit >= 5) return 5;
            if (digit >= 3) return 3;
            return 1;
        }

        // Attributes (Str / Dex / Int / All)
        if (ContainsAny(r, "strength", "dexterity", "intelligence", "attribute") ||
            ContainsAny(n, "strength", "dexterity", "intelligence", "attribute"))
        {
            if (digit >= 5) return 4;
            if (digit >= 3) return 3;
            return 1;
        }

        // Evasion Rating (solo, non-hybrid)
        if ((r.Contains("evasion") || n.Contains("evasion")) && !isHybridDefence)
        {
            if (digit >= 5) return 5;
            if (digit >= 3) return 3;
            return 1;
        }

        // Armour Rating (solo, non-hybrid)
        if ((ContainsAny(r, "armour", "armor") || ContainsAny(n, "armour", "armor")) && !isHybridDefence)
        {
            if (digit >= 5) return 5;
            if (digit >= 3) return 3;
            return 1;
        }

        // Energy Shield (solo, non-hybrid)
        if ((r.Contains("energyshield") || n.Contains("energyshield") ||
             (r.Contains("energy") && r.Contains("shield")) ||
             (n.Contains("energy") && n.Contains("shield"))) && !isHybridDefence)
        {
            if (digit >= 5) return 5;
            if (digit >= 3) return 4;
            return 2;
        }

        // Projectile Speed / Pierce
        if (ContainsAny(r, "pierce") || ContainsAny(n, "pierce"))
            return 3;
        if ((r.Contains("project") || n.Contains("project")) && !ContainsAny(r, "damage") && !ContainsAny(n, "damage"))
            return 3;

        // Ailment mods — AVERAGE
        if (ContainsAny(r, "ailment", "freeze", "shock", "bleed", "poison", "ignite") ||
            ContainsAny(n, "ailment", "freeze", "shock", "bleed", "poison", "ignite"))
        {
            if (digit >= 5) return 4;
            if (digit >= 3) return 2;
            return 1;
        }

        // Charm (belt implicits) — AVERAGE
        if (r.Contains("charm") || n.Contains("charm"))
            return 3;

        // Mana flat -- AVERAGE only for caster weapons/off-hands, LOW (score=1) elsewhere
        if ((r.Contains("mana") || n.Contains("mana")) && !ContainsAny(r, "regene") && !ContainsAny(n, "regene"))
        {
            bool isCasterSlot = cls.Contains("wand") || cls.Contains("staves") ||
                                cls.Contains("foci") || cls.Contains("focus") || cls.Contains("staff");
            if (isCasterSlot)
            {
                if (digit >= 5) return 4;
                if (digit >= 3) return 2;
            }
            return 1;
        }

        // ── LOW / SOFT EXCLUDE ───────────────────────────────────────────────────

        // Life Regen — LOW (score=1, isGood=false)
        if ((r.Contains("life") || n.Contains("life")) && (r.Contains("regene") || n.Contains("regene")))
            return 1;

        // Default fallback — unknown mod, treat as low value
        if (digit >= 5) return 3;
        if (digit >= 3) return 2;
        return 1;
    }

    private Color? EvaluateItem(ItemData itemData, out string debugInfo)
    {
        debugInfo = "";
        if (itemData?.ModsInfo == null) return null;

        int totalRelevantMods = 0;
        int goodTiersCount = 0;
        int rawScore = 0;

        var detailedMods = new List<string>();

        // Gather all mods (both Explicits AND Implicits as requested!) with strict deduplication
        var allMods = new List<ItemMod>();
        var seenRaw = new HashSet<string>();

        if (itemData.ModsInfo.ItemMods != null)
        {
            foreach (var mod in itemData.ModsInfo.ItemMods)
            {
                if (mod != null)
                {
                    string modKey = $"{mod.RawName}_{mod.Name}";
                    if (seenRaw.Add(modKey))
                    {
                        allMods.Add(mod);
                    }
                }
            }
        }

        if (itemData.ModsInfo.ImplicitMods != null)
        {
            foreach (var mod in itemData.ModsInfo.ImplicitMods)
            {
                if (mod != null)
                {
                    string modKey = $"{mod.RawName}_{mod.Name}";
                    if (seenRaw.Add(modKey))
                    {
                        allMods.Add(mod);
                    }
                }
            }
        }

        var activeProfile = _profileManager?.GetBuildProfile(Settings.ActiveBuildProfile.Value);
        int comboModCount = 0;

        foreach (var mod in allMods)
        {
            bool relevant = IsModRelevant(itemData.ClassName, mod.Name, mod.RawName);
            if (relevant)
            {
                int rawDigit = ParseTier(mod.RawName);
                bool isGood = false;

                int points = GetModScore(mod.Name, mod.RawName, rawDigit, itemData.ClassName, out isGood);

                totalRelevantMods++;
                if (isGood) goodTiersCount++;
                rawScore += points;

                detailedMods.Add($"{mod.Name} [AbsTier {rawDigit} (+{points}pts)]");

                if (IsOffensiveSlot(itemData.ClassName) &&
                    ModMatchesComboKeyword(
                        (mod.RawName ?? "").ToLowerInvariant(),
                        (mod.Name ?? "").ToLowerInvariant(),
                        activeProfile?.ComboMods))
                    comboModCount++;
            }
        }

        // Density bonus — rewards having many relevant mods but cannot compensate for low quality.
        int bonus = 0;
        if (totalRelevantMods >= 6) bonus = 15;
        else if (totalRelevantMods == 5) bonus = 10;
        else if (totalRelevantMods == 4) bonus = 6;
        else if (totalRelevantMods == 3) bonus = 3;
        else if (totalRelevantMods == 2) bonus = 1;

        // Combo bonus — synergistic offensive mods matching the active build profile
        int comboBonus = 0;
        if (comboModCount >= 3) comboBonus = 12;
        else if (comboModCount >= 2) comboBonus = 6;

        int totalScore = rawScore + bonus + comboBonus;

        bool isRare = itemData.Rarity == ItemRarity.Rare;
        bool isMagic = itemData.Rarity == ItemRarity.Magic;

        string classification = "None";
        Color? retColor = null;

        var ruleGod = Settings.Rules.FirstOrDefault(r => r.Enabled && r.Name.Equals("GOD Tier", StringComparison.OrdinalIgnoreCase));
        var ruleBis = Settings.Rules.FirstOrDefault(r => r.Enabled && r.Name.Equals("BiS", StringComparison.OrdinalIgnoreCase));
        var ruleTrade = Settings.Rules.FirstOrDefault(r => r.Enabled && r.Name.Equals("Trade", StringComparison.OrdinalIgnoreCase));
        var ruleCraftCiano = Settings.Rules.FirstOrDefault(r => r.Enabled && r.Name.Equals("Craft Ciano", StringComparison.OrdinalIgnoreCase));
        var ruleCraftBase = Settings.Rules.FirstOrDefault(r => r.Enabled && r.Name.Equals("Craft Base", StringComparison.OrdinalIgnoreCase));

        if (isRare)
        {
            if (ruleGod != null && totalScore >= 75)
            {
                classification = "GOD Tier";
                retColor = ruleGod.Color.ToColor();
            }
            else if (ruleBis != null && totalScore >= 50)
            {
                classification = "BiS";
                retColor = ruleBis.Color.ToColor();
            }
            else if (ruleTrade != null && totalScore >= 30)
            {
                classification = "Trade";
                retColor = ruleTrade.Color.ToColor();
            }
        }
        else if (isMagic)
        {
            if (ruleCraftCiano != null && goodTiersCount >= 2)
            {
                classification = "Craft Ciano";
                retColor = ruleCraftCiano.Color.ToColor();
            }
            else if (ruleCraftBase != null && goodTiersCount >= 1)
            {
                classification = "Craft Base";
                retColor = ruleCraftBase.Color.ToColor();
            }
        }

        string profileName = Settings.ActiveBuildProfile?.Value ?? "Generic";
        debugInfo = $"Classification: {classification}\nScore: {totalScore}/100 (Raw: {rawScore} + Density: {bonus} + Combo: {comboBonus}) | GoodTiers: {goodTiersCount} | ComboMods: {comboModCount} | RelevantMods: {totalRelevantMods}\nProfile: {profileName}\nMods:\n  * {string.Join("\n  * ", detailedMods)}";

        return retColor;
    }

    public override void Render()
    {
        if (!Settings.Enable || _inventoryCache == null) return;

        var inventoryPanel = GameController?.IngameState?.IngameUi?.InventoryPanel;
        if (inventoryPanel == null || !inventoryPanel.IsVisible) return;

        var items = _inventoryCache.Value;
        if (items == null || items.Count == 0) return;

        var mousePos = ImGui.GetMousePos();

        foreach (var slotItem in items.ToList())
        {
            var entity = slotItem?.Item;
            if (entity == null || entity.Address == 0 || !entity.IsValid) continue;

            ItemData itemData = null;
            try { itemData = new ItemData(entity, GameController); } catch { continue; }

            string debugInfo = "";
            var highlightColor = EvaluateItem(itemData, out debugInfo);

            if (Settings.DebugMode)
            {
                var rect = slotItem.GetClientRect();
                if (rect.X <= mousePos.X && mousePos.X <= rect.X + rect.Width &&
                    rect.Y <= mousePos.Y && mousePos.Y <= rect.Y + rect.Height)
                {
                    DrawModDebug(itemData, debugInfo, mousePos);
                }
            }

            if (highlightColor.HasValue)
            {
                DrawHighlight(slotItem, highlightColor.Value);
            }
        }
    }

    private void DrawModDebug(ItemData itemData, string debugInfo, Vector2 mousePos)
    {
        try
        {
            var lines = new List<string>
            {
                $"ClassName: {itemData.ClassName}",
                $"BaseName: {itemData.BaseName}",
                $"Rarity: {itemData.Rarity}",
                "--- Evaluation ---",
                debugInfo
            };

            var pos = new Vector2(mousePos.X - 320, mousePos.Y + 50);
            ImGui.SetNextWindowPos(pos);
            ImGui.SetNextWindowBgAlpha(0.85f);
            ImGui.Begin("##bfdebug", ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize |
                ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNav);
            foreach (var line in lines)
            {
                if (line.Contains('\n'))
                {
                    foreach (var sub in line.Split('\n'))
                        ImGui.Text(sub);
                }
                else
                {
                    ImGui.Text(line);
                }
            }
            ImGui.End();
        }
        catch { }
    }

    private void DrawHighlight(ServerInventory.InventSlotItem slotItem, Color color)
    {
        var rect = slotItem.GetClientRect();
        if (rect.Width <= 0 || rect.Height <= 0) return;

        var deflateFactor = Settings.BorderDeflation.Value / 200.0;
        var deflateWidth = (int)(rect.Width * deflateFactor + Settings.BorderThickness.Value / 2.0);
        var deflateHeight = (int)(rect.Height * deflateFactor + Settings.BorderThickness.Value / 2.0);
        rect.Inflate(-deflateWidth, -deflateHeight);

        Graphics.DrawFrame(rect, color, Settings.BorderThickness.Value);
    }

    public override void DrawSettings()
    {
        base.DrawSettings();
        ImGui.Separator();

        // Build Profile selector
        ImGui.Text("Build Profile  (affects Combo Bonus)");
        var buildProfileNames = _profileManager?.BuildProfileNames ?? new System.Collections.Generic.List<string> { "Generic" };
        int currentProfileIdx = buildProfileNames.IndexOf(Settings.ActiveBuildProfile.Value);
        if (currentProfileIdx < 0) currentProfileIdx = 0;
        var profileArray = buildProfileNames.ToArray();
        if (ImGui.Combo("##buildprofile", ref currentProfileIdx, profileArray, profileArray.Length))
            Settings.ActiveBuildProfile.Value = profileArray[currentProfileIdx];

        var activeProf = _profileManager?.GetBuildProfile(Settings.ActiveBuildProfile.Value);
        if (activeProf != null && !string.IsNullOrEmpty(activeProf.Description))
            ImGui.TextDisabled(activeProf.Description);

        ImGui.Separator();
        ImGui.Text("Filter Rules  (first match wins)");
        ImGui.TextDisabled("All enabled rules are always active simultaneously.");

        var debugMode = Settings.DebugMode;
        if (ImGui.Checkbox("Debug Mode (hover item to see mod names & tiers)", ref debugMode))
            Settings.DebugMode = debugMode;

        ImGui.Separator();

        for (var i = 0; i < Settings.Rules.Count; i++)
        {
            var rule = Settings.Rules[i];
            ImGui.PushID(i);

            var enabled = rule.Enabled;
            if (ImGui.Checkbox("##en", ref enabled)) { rule.Enabled = enabled; }
            ImGui.SameLine();

            var col = rule.Color.ToVector4();
            if (ImGui.ColorEdit4("##col", ref col, ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.NoLabel))
            {
                rule.Color = new SerializableColor { R = (int)(col.X * 255), G = (int)(col.Y * 255), B = (int)(col.Z * 255), A = (int)(col.W * 255) };
            }
            ImGui.SameLine();

            ImGui.Text(rule.Name);

            ImGui.PopID();
        }
    }

    private List<ServerInventory.InventSlotItem> GetInventoryItems()
    {
        try
        {
            var playerInventories = GameController?.IngameState?.ServerData?.PlayerInventories;
            if (playerInventories == null || playerInventories.Count == 0) return new();
            var mainInv = playerInventories.FirstOrDefault(x =>
                x?.Inventory?.InventSlot == InventorySlotE.MainInventory1);
            return mainInv?.Inventory?.InventorySlotItems?.ToList() ?? new();
        }
        catch { return new(); }
    }
}
