using System;
using System.Collections.Generic;
using System.Linq;

using Server.Accounting;
using Server.Engines.CharacterStates;

namespace Server.Engines.WebAdmin
{
    /// <summary>
    ///     Character state operations for the web admin. Every method here assumes it is running on
    ///     the game thread, as <see cref="AccountAdmin" /> does.
    ///     <para />
    ///     A character is addressed as an account and a slot, never by name. Character names are not
    ///     unique on a shard, and several bots hold a character each.
    /// </summary>
    public static class StateAdmin
    {
        public static List<StateInfo> States()
        {
            return CharacterState.List();
        }

        public static Mobile Character(Account account, int slot)
        {
            if (account == null || slot < 0 || slot >= account.Length)
            {
                return null;
            }

            return account[slot];
        }

        public static AdminResult Save(string username, int slot, string name, bool overwrite)
        {
            Mobile m;

            var refusal = Resolve(username, slot, out m);

            if (refusal != null)
            {
                return refusal;
            }

            if (!CharacterState.IsValidName(name))
            {
                return AdminResult.Fail("{0}", CharacterState.NameRule);
            }

            if (!overwrite && CharacterState.Exists(name))
            {
                return AdminResult.Fail("A state named {0} already exists. Tick overwrite to replace it.", name);
            }

            var notes = new List<string>();
            var error = CharacterState.Save(m, name, notes);

            if (error != null)
            {
                return AdminResult.Fail("{0}", error);
            }

            return AdminResult.Done("Saved {0} as {1}.{2}", m.Name, name, Notes(notes));
        }

        public static AdminResult Restore(string username, int slot, string name)
        {
            Mobile m;

            var refusal = Resolve(username, slot, out m);

            if (refusal != null)
            {
                return refusal;
            }

            // A live restore would leave the client holding a view the server no longer has, so the
            // caller logs the character out first. AccountAdmin.Delete refuses on the same grounds.
            if (m.NetState != null)
            {
                return AdminResult.Fail("{0} is still playing. Log out first.", m.Name);
            }

            var notes = new List<string>();
            var error = CharacterState.Load(m, name, notes);

            if (error != null)
            {
                return AdminResult.Fail("{0}", error);
            }

            return AdminResult.Done("Restored {0} from {1}.{2}", m.Name, name, Notes(notes));
        }

        public static AdminResult Delete(string name)
        {
            var error = CharacterState.Remove(name);

            if (error != null)
            {
                return AdminResult.Fail("{0}", error);
            }

            return AdminResult.Done("Deleted the state {0}.", name);
        }

        private static AdminResult Resolve(string username, int slot, out Mobile m)
        {
            var account = AccountAdmin.Find(username);

            m = null;

            if (account == null)
            {
                return AdminResult.Fail("Account {0} does not exist.", username);
            }

            m = Character(account, slot);

            if (m == null)
            {
                return AdminResult.Fail("{0} has no character in slot {1}.", account.Username, slot);
            }

            return null;
        }

        /// <summary>
        ///     The first few notes, short enough to travel in a redirect.
        /// </summary>
        private static string Notes(List<string> notes)
        {
            if (notes.Count == 0)
            {
                return String.Empty;
            }

            var shown = String.Join(" ", notes.Take(3));

            if (notes.Count > 3)
            {
                shown += String.Format(" {0} more.", notes.Count - 3);
            }

            return " " + shown;
        }
    }
}
