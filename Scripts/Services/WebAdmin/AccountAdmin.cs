using System;
using System.Collections.Generic;
using System.Linq;

using Server.Accounting;
using Server.Misc;

namespace Server.Engines.WebAdmin
{
    public class AdminResult
    {
        private AdminResult(bool ok, string message)
        {
            Ok = ok;
            Message = message;
        }

        public bool Ok { get; private set; }
        public string Message { get; private set; }

        public static AdminResult Done(string format, params object[] args)
        {
            return new AdminResult(true, String.Format(format, args));
        }

        public static AdminResult Fail(string format, params object[] args)
        {
            return new AdminResult(false, String.Format(format, args));
        }
    }

    /// <summary>
    ///     Account mutations for the web admin. Every method here assumes it is running on the game
    ///     thread and persists its own change; the caller is responsible for the marshalling.
    /// </summary>
    public static class AccountAdmin
    {
        public static AdminResult Create(string username, string password, string confirm, AccessLevel level)
        {
            username = (username ?? String.Empty).Trim();

            var invalid = ValidateCredentials(username, password, confirm);

            if (invalid != null)
            {
                return invalid;
            }

            if (Accounts.GetAccount(username) != null)
            {
                return AdminResult.Fail("Account {0} already exists.", username);
            }

            var account = new Account(username, password) { AccessLevel = level };

            account.Comments.Add(new AccountComment(CommentAuthor, "Created via web admin."));

            Persist();

            return AdminResult.Done("Account {0} created with access level {1}.", account.Username, level);
        }

        public static AdminResult SetPassword(string username, string password, string confirm)
        {
            var account = Find(username);

            if (account == null)
            {
                return AdminResult.Fail("Account {0} does not exist.", username);
            }

            var invalid = ValidatePassword(password, confirm);

            if (invalid != null)
            {
                return invalid;
            }

            // Hashing is the Account's job: it applies whatever PasswordProtection the shard runs.
            account.SetPassword(password);

            account.Comments.Add(new AccountComment(CommentAuthor, "Password changed via web admin."));

            Persist();

            return AdminResult.Done("Password set for {0}.", account.Username);
        }

        public static AdminResult SetPrivileges(string username, AccessLevel level, bool banned, string ipRestrictions)
        {
            var account = Find(username);

            if (account == null)
            {
                return AdminResult.Fail("Account {0} does not exist.", username);
            }

            if (account.AccessLevel != level && IsLastOwner(account))
            {
                return AdminResult.Fail("{0} is the only owner account; its access level cannot be lowered.", account.Username);
            }

            if (banned && !account.Banned && IsLastOwner(account))
            {
                return AdminResult.Fail("{0} is the only owner account; it cannot be banned.", account.Username);
            }

            var changes = new List<string>();

            if (account.AccessLevel != level)
            {
                changes.Add(String.Format("access level {0} to {1}", account.AccessLevel, level));
                account.AccessLevel = level;
            }

            if (account.Banned != banned)
            {
                changes.Add(banned ? "banned" : "unbanned");
                account.Banned = banned;
            }

            var rejected = new List<string>();
            var accepted = ParseIPRestrictions(ipRestrictions, rejected);

            if (!accepted.SequenceEqual(account.IPRestrictions, StringComparer.Ordinal))
            {
                changes.Add(String.Format("{0} IP restriction{1}", accepted.Length, accepted.Length == 1 ? "" : "s"));
                account.IPRestrictions = accepted;
            }

            if (changes.Count == 0 && rejected.Count == 0)
            {
                return AdminResult.Done("No changes for {0}.", account.Username);
            }

            if (changes.Count > 0)
            {
                account.Comments.Add(new AccountComment(CommentAuthor, "Web admin: " + String.Join(", ", changes) + "."));
                Persist();
            }

            if (rejected.Count > 0)
            {
                return AdminResult.Fail("Not a valid IP or IP mask: {0}.", String.Join(", ", rejected));
            }

            return AdminResult.Done("{0} updated: {1}.", account.Username, String.Join(", ", changes));
        }

        public static AdminResult Delete(string username)
        {
            var account = Find(username);

            if (account == null)
            {
                return AdminResult.Fail("Account {0} does not exist.", username);
            }

            if (IsLastOwner(account))
            {
                return AdminResult.Fail("{0} is the only owner account and cannot be deleted.", account.Username);
            }

            var online = OnlineCharacters(account);

            if (online.Count > 0)
            {
                return AdminResult.Fail("{0} is still playing as {1}.", account.Username, String.Join(", ", online));
            }

            var name = account.Username;

            account.Delete();

            // Deletion destroys mobiles and houses, which only a world save writes out. AutoSave
            // rotates the pre-deletion world into the backups first; this is the one irreversible
            // operation here, so it is worth the extra write.
            AutoSave.Save();

            return AdminResult.Done("Account {0} and its characters were deleted.", name);
        }

        public static Account Find(string username)
        {
            return Accounts.GetAccount((username ?? String.Empty).Trim()) as Account;
        }

        public static List<Account> All()
        {
            return Accounts.GetAccounts()
                           .OfType<Account>()
                           .OrderBy(a => a.Username, StringComparer.OrdinalIgnoreCase)
                           .ToList();
        }

        public static List<string> OnlineCharacters(Account account)
        {
            var names = new List<string>();

            for (var i = 0; i < account.Length; ++i)
            {
                var m = account[i];

                if (m != null && m.NetState != null)
                {
                    names.Add(m.Name);
                }
            }

            return names;
        }

        public static List<Mobile> Characters(Account account)
        {
            var mobiles = new List<Mobile>();

            for (var i = 0; i < account.Length; ++i)
            {
                var m = account[i];

                if (m != null)
                {
                    mobiles.Add(m);
                }
            }

            return mobiles;
        }

        public static bool IsLastOwner(Account account)
        {
            return account.AccessLevel == AccessLevel.Owner &&
                   Accounts.GetAccounts().Count(a => a.AccessLevel == AccessLevel.Owner) == 1;
        }

        private const string CommentAuthor = "web admin";

        private static AdminResult ValidateCredentials(string username, string password, string confirm)
        {
            if (username.Length == 0)
            {
                return AdminResult.Fail("A username is required.");
            }

            if (username.EndsWith("."))
            {
                return AdminResult.Fail("A username may not end with a period.");
            }

            if (username.Any(c => c < 0x20 || c >= 0x7F || AccountHandler.IsForbiddenChar(c)))
            {
                return AdminResult.Fail("The username contains a character the login server rejects.");
            }

            return ValidatePassword(password, confirm);
        }

        private static AdminResult ValidatePassword(string password, string confirm)
        {
            if (String.IsNullOrEmpty(password))
            {
                return AdminResult.Fail("A password is required.");
            }

            if (password != confirm)
            {
                return AdminResult.Fail("The two passwords do not match.");
            }

            if (password.Any(c => c < 0x20 || c >= 0x7F))
            {
                return AdminResult.Fail("The password must be printable ASCII; the client cannot send anything else.");
            }

            return null;
        }

        private static string[] ParseIPRestrictions(string text, List<string> rejected)
        {
            var accepted = new List<string>();

            foreach (var line in (text ?? String.Empty).Split('\n'))
            {
                var entry = line.Trim();

                if (entry.Length == 0)
                {
                    continue;
                }

                if (Utility.IsValidIP(entry))
                {
                    accepted.Add(entry);
                }
                else
                {
                    rejected.Add(entry);
                }
            }

            return accepted.ToArray();
        }

        private static void Persist()
        {
            Accounts.Save(new WorldSaveEventArgs(false));
        }
    }
}
