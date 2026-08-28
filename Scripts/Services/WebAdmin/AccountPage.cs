using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Web;

using Server.Accounting;
using Server.Misc;
using Server.Multis;

namespace Server.Engines.WebAdmin
{
    /// <summary>
    ///     Server-rendered markup for the account admin, built from the Home Lab (AKC12) design
    ///     system class names. Runs on the game thread, so it may read accounts directly.
    /// </summary>
    public static class AccountPage
    {
        public static string Index(string notice, bool failed)
        {
            var accounts = AccountAdmin.All();
            var online = accounts.Sum(a => AccountAdmin.OnlineCharacters(a).Count);

            var html = new StringBuilder();

            Open(html, "Accounts", accounts.Count, online);

            html.Append("<main class=\"bay\">");

            Toast(html, notice, failed);

            PanelHead(html, "Accounts", String.Format("{0} total, {1} online", accounts.Count, online));
            html.Append("<div class=\"hl-panel__body\"><div class=\"table-scroll\"><table class=\"acct\">");
            html.Append("<thead><tr><th>Username</th><th>Access</th><th>State</th><th>Characters</th>");
            html.Append("<th>Created</th><th>Last login</th><th>IP limits</th><th></th></tr></thead><tbody>");

            foreach (var account in accounts)
            {
                Row(html, account);
            }

            if (accounts.Count == 0)
            {
                html.Append("<tr><td colspan=\"8\" class=\"muted\">No accounts.</td></tr>");
            }

            html.Append("</tbody></table></div></div></section>");

            CreatePanel(html);

            html.Append("</main></div></body></html>");

            return html.ToString();
        }

        public static string Detail(Account account, string notice, bool failed)
        {
            var characters = AccountAdmin.Characters(account);

            var html = new StringBuilder();

            Open(html, account.Username);

            html.Append("<main class=\"bay\">");

            Toast(html, notice, failed);

            html.Append("<div class=\"form-row\"><a class=\"hl-btn hl-btn--sm hl-btn--ghost\" href=\"/\">All accounts</a>");
            html.AppendFormat("<h2>{0}</h2></div>", Encode(account.Username));

            PanelHead(html, "Record", AccessBadge(account.AccessLevel) + " " + StateLed(account));
            html.Append("<div class=\"hl-panel__body\"><div class=\"readouts\">");
            Readout(html, "Characters", characters.Count + " / " + account.Length, null);
            Readout(html, "Created", account.Created.ToString("yyyy-MM-dd"), account.Created.ToString("HH:mm") + " UTC");
            Readout(html, "Last login", account.LastLogin.ToString("yyyy-MM-dd"), account.LastLogin.ToString("HH:mm") + " UTC");
            Readout(html, "Last login IP", account.LoginIPs.Length > 0 ? account.LoginIPs[0].ToString() : "none", null);
            html.Append("</div>");

            if (characters.Count > 0)
            {
                html.Append("<div class=\"form-row\" style=\"margin-top:var(--sp-6)\">");
                html.Append("<span class=\"hl-legend\">Characters</span>");

                foreach (var m in characters)
                {
                    html.AppendFormat("<span class=\"hl-tag\">{0}</span>", Encode(m.Name));
                }

                html.Append("</div>");
            }

            html.Append("</div></section>");

            PasswordPanel(html, account.Username);
            PrivilegesPanel(html, account);
            DangerPanel(html, account.Username);

            html.Append("</main></div></body></html>");

            return html.ToString();
        }

        public static string DeleteConfirm(Account account)
        {
            var characters = AccountAdmin.Characters(account);

            var houses = 0;

            foreach (var m in characters)
            {
                var list = BaseHouse.GetHouses(m);

                houses += list.Count;

                ColUtility.Free(list);
            }

            var html = new StringBuilder();

            Open(html, "Delete " + account.Username);

            html.Append("<main class=\"bay\">");
            html.Append("<section class=\"hl-panel hl-panel--trim hl-ch-magenta\"><span class=\"hl-panel__trim\"></span>");
            html.Append("<header class=\"hl-panel__head\"><h3 class=\"hl-panel__title\">Delete account</h3></header>");
            html.Append("<div class=\"hl-panel__body\">");

            html.AppendFormat("<p>Deleting <b>{0}</b> also deletes {1} character{2} and {3} house{4}. "
                              + "The characters and their contents are destroyed, not archived.</p>",
                              Encode(account.Username),
                              characters.Count, characters.Count == 1 ? "" : "s",
                              houses, houses == 1 ? "" : "s");

            if (characters.Count > 0)
            {
                html.Append("<div class=\"form-row\">");

                foreach (var m in characters)
                {
                    html.AppendFormat("<span class=\"hl-tag\">{0}</span>", Encode(m.Name));
                }

                html.Append("</div>");
            }

            html.Append("<form method=\"post\" action=\"/delete\" class=\"form-actions\">");
            html.AppendFormat("<input type=\"hidden\" name=\"username\" value=\"{0}\">", Encode(account.Username));
            html.Append("<button class=\"hl-btn hl-btn--hot\" type=\"submit\">Delete account</button>");
            html.AppendFormat("<a class=\"hl-btn hl-btn--ghost\" href=\"/account?u={0}\">Cancel</a>", Url(account.Username));
            html.Append("</form></div></section></main></div></body></html>");

            return html.ToString();
        }

        public static string NotFound(string username)
        {
            var html = new StringBuilder();

            Open(html, "Not found");

            html.Append("<main class=\"bay\"><section class=\"hl-panel\">");
            html.Append("<header class=\"hl-panel__head\"><h3 class=\"hl-panel__title\">Not found</h3></header>");
            html.AppendFormat("<div class=\"hl-panel__body\"><p>No account named <b>{0}</b>.</p>"
                              + "<a class=\"hl-btn hl-btn--ghost\" href=\"/\">All accounts</a></div>",
                              Encode(username));
            html.Append("</section></main></div></body></html>");

            return html.ToString();
        }

        #region Sections

        private static void Row(StringBuilder html, Account account)
        {
            var characters = AccountAdmin.Characters(account);
            var url = Url(account.Username);

            html.Append("<tr>");
            html.AppendFormat("<td class=\"acct__user\">{0}</td>", Encode(account.Username));
            html.AppendFormat("<td>{0}</td>", AccessBadge(account.AccessLevel));
            html.AppendFormat("<td>{0}</td>", StateLed(account));
            html.AppendFormat("<td class=\"acct__num\">{0} / {1}</td>", characters.Count, account.Length);
            html.AppendFormat("<td class=\"acct__num acct__num--quiet\">{0}</td>", account.Created.ToString("yyyy-MM-dd HH:mm"));
            html.AppendFormat("<td class=\"acct__num acct__num--quiet\">{0}</td>", account.LastLogin.ToString("yyyy-MM-dd HH:mm"));
            html.AppendFormat("<td class=\"acct__num acct__num--quiet\">{0}</td>",
                              account.IPRestrictions.Length == 0 ? "none" : account.IPRestrictions.Length.ToString());
            html.AppendFormat("<td><div class=\"acct__actions\">"
                              + "<a class=\"hl-btn hl-btn--sm hl-btn--ghost\" href=\"/account?u={0}\">Manage</a></div></td>", url);
            html.Append("</tr>");
        }

        private static void CreatePanel(StringBuilder html)
        {
            PanelHead(html, "New account", null);
            html.Append("<div class=\"hl-panel__body\"><form method=\"post\" action=\"/create\">");
            html.Append("<div class=\"form-grid\">");

            Field(html, "Username", "<input class=\"hl-input__control\" type=\"text\" name=\"username\" autocomplete=\"off\" required>");
            Field(html, "Password", "<input class=\"hl-input__control\" type=\"password\" name=\"password\" autocomplete=\"new-password\" required>");
            Field(html, "Confirm password", "<input class=\"hl-input__control\" type=\"password\" name=\"confirm\" autocomplete=\"new-password\" required>");

            html.Append("<label class=\"hl-field\"><span class=\"hl-field__label\">Access level</span>");
            AccessSelect(html, AccessLevel.Player);
            html.Append("</label>");

            html.Append("</div><div class=\"form-actions\">");
            html.Append("<button class=\"hl-btn hl-btn--primary\" type=\"submit\">Create account</button>");
            html.Append("</div></form></div></section>");
        }

        private static void PasswordPanel(StringBuilder html, string username)
        {
            html.Append("<section class=\"hl-panel hl-ch-magenta\">");
            html.Append("<header class=\"hl-panel__head\"><h3 class=\"hl-panel__title\">Password</h3>");
            html.Append("<span class=\"hl-panel__meta\">stored hashed, never recoverable</span></header>");
            html.Append("<div class=\"hl-panel__body\"><form method=\"post\" action=\"/password\">");
            html.AppendFormat("<input type=\"hidden\" name=\"username\" value=\"{0}\">", Encode(username));
            html.Append("<div class=\"form-grid\">");

            Field(html, "New password", "<input class=\"hl-input__control\" type=\"password\" name=\"password\" autocomplete=\"new-password\" required>");
            Field(html, "Confirm password", "<input class=\"hl-input__control\" type=\"password\" name=\"confirm\" autocomplete=\"new-password\" required>");

            html.Append("</div><div class=\"form-actions\">");
            html.Append("<button class=\"hl-btn hl-btn--primary\" type=\"submit\">Set password</button>");
            html.Append("</div></form></div></section>");
        }

        private static void PrivilegesPanel(StringBuilder html, Account account)
        {
            PanelHead(html, "Privileges", null);
            html.Append("<div class=\"hl-panel__body\"><form method=\"post\" action=\"/privileges\">");
            html.AppendFormat("<input type=\"hidden\" name=\"username\" value=\"{0}\">", Encode(account.Username));
            html.Append("<div class=\"form-grid\">");

            html.Append("<label class=\"hl-field\"><span class=\"hl-field__label\">Access level</span>");
            AccessSelect(html, account.AccessLevel);
            html.Append("</label>");

            html.Append("<label class=\"hl-switch\">");
            html.AppendFormat("<input type=\"checkbox\" name=\"banned\" value=\"1\"{0}>", account.Banned ? " checked" : "");
            html.Append("<span class=\"hl-switch__track\"><span class=\"hl-switch__thumb\"></span></span>");
            html.Append("<span class=\"hl-switch__legend\">Banned</span></label>");

            html.Append("<label class=\"hl-field\" style=\"grid-column:1/-1\">");
            html.Append("<span class=\"hl-field__label\">IP restrictions</span>");
            html.Append("<span class=\"hl-input hl-input--multiline\">");
            html.AppendFormat("<textarea class=\"hl-input__control\" name=\"ips\" rows=\"3\" spellcheck=\"false\">{0}</textarea>",
                              Encode(String.Join("\n", account.IPRestrictions)));
            html.Append("</span>");
            html.Append("<span class=\"hl-field__hint\">One address or mask per line, e.g. 192.168.1.* — empty means no restriction.</span>");
            html.Append("</label>");

            html.Append("</div><div class=\"form-actions\">");
            html.Append("<button class=\"hl-btn hl-btn--primary\" type=\"submit\">Save privileges</button>");
            html.Append("</div></form></div></section>");
        }

        private static void DangerPanel(StringBuilder html, string username)
        {
            html.Append("<section class=\"hl-panel hl-ch-magenta\">");
            html.Append("<header class=\"hl-panel__head\"><h3 class=\"hl-panel__title\">Delete</h3></header>");
            html.Append("<div class=\"hl-panel__body\">");
            html.Append("<p>Deletes the account, its characters and their houses. There is no undo.</p>");
            html.AppendFormat("<a class=\"hl-btn hl-btn--hot\" href=\"/account/delete?u={0}\">Delete account</a>", Url(username));
            html.Append("</div></section>");
        }

        #endregion

        #region Fragments

        private static void Open(StringBuilder html, string title)
        {
            Open(html, title, -1, -1);
        }

        private static void Open(StringBuilder html, string title, int accountCount, int online)
        {
            html.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\">");
            html.Append("<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
            html.AppendFormat("<title>{0} — {1}</title>", Encode(title), Encode(ServerList.ServerName));
            html.Append("<link rel=\"stylesheet\" href=\"/homelab.css\"><link rel=\"stylesheet\" href=\"/accounts.css\">");
            html.Append("</head><body><div class=\"page\">");

            html.Append("<header class=\"chassis\">");
            html.AppendFormat("<a class=\"chassis__brand\" href=\"/\">{0}</a>", Encode(ServerList.ServerName));
            html.Append("<span class=\"chassis__sub\">ACCOUNTS</span>");
            html.Append("<div class=\"chassis__lamps\">");
            Led(html, "green", true, false, "Listening");

            if (accountCount >= 0)
            {
                Led(html, "amber", online > 0, online > 0, "Players");
                html.AppendFormat("<span class=\"acct__num\">{0} accounts, {1} online</span>", accountCount, online);
            }

            html.Append("</div></header>");
        }

        private static void PanelHead(StringBuilder html, string title, string meta)
        {
            html.Append("<section class=\"hl-panel\"><header class=\"hl-panel__head\">");
            html.AppendFormat("<h3 class=\"hl-panel__title\">{0}</h3>", Encode(title));

            if (!String.IsNullOrEmpty(meta))
            {
                html.AppendFormat("<span class=\"hl-panel__meta\">{0}</span>", meta);
            }

            html.Append("</header>");
        }

        private static void Field(StringBuilder html, string label, string control)
        {
            html.AppendFormat("<label class=\"hl-field\"><span class=\"hl-field__label\">{0}</span>"
                              + "<span class=\"hl-input\">{1}</span></label>", Encode(label), control);
        }

        private static void AccessSelect(StringBuilder html, AccessLevel selected)
        {
            html.Append("<span class=\"hl-select\"><select class=\"hl-select__control\" name=\"level\">");

            foreach (AccessLevel level in Enum.GetValues(typeof(AccessLevel)))
            {
                html.AppendFormat("<option value=\"{0}\"{1}>{0}</option>", level, level == selected ? " selected" : "");
            }

            html.Append("</select><span class=\"hl-select__chevron\"></span></span>");
        }

        private static void Readout(StringBuilder html, string label, string value, string unit)
        {
            html.Append("<span class=\"hl-readout hl-readout--sm\">");
            html.AppendFormat("<span class=\"hl-readout__label\">{0}</span>", Encode(label));
            html.AppendFormat("<span class=\"hl-readout__value\">{0}", Encode(value));

            if (!String.IsNullOrEmpty(unit))
            {
                html.AppendFormat("<span class=\"hl-readout__unit\">{0}</span>", Encode(unit));
            }

            html.Append("</span></span>");
        }

        private static void Led(StringBuilder html, string tone, bool on, bool blink, string label)
        {
            html.AppendFormat("<span class=\"hl-led\" data-on=\"{0}\" data-blink=\"{1}\" style=\"color:var(--led-{2})\">"
                              + "<span class=\"hl-led__lamp\"></span>{3}</span>",
                              on ? "true" : "false", blink ? "true" : "false", tone, Encode(label));
        }

        private static void Toast(StringBuilder html, string notice, bool failed)
        {
            if (String.IsNullOrEmpty(notice))
            {
                return;
            }

            html.AppendFormat("<div class=\"hl-toast-stack\"><div class=\"hl-toast{0}\"><span class=\"hl-toast__rail\"></span>"
                              + "<div class=\"hl-toast__body\"><div class=\"hl-toast__title\">{1}</div>"
                              + "<div class=\"hl-toast__msg\">{2}</div></div></div></div>",
                              failed ? " hl-toast--error" : "",
                              failed ? "Rejected" : "Done",
                              Encode(notice));
        }

        private static string AccessBadge(AccessLevel level)
        {
            string tone;

            if (level >= AccessLevel.Administrator)
            {
                tone = " hl-badge--hot";
            }
            else if (level >= AccessLevel.Counselor)
            {
                tone = " hl-badge--info";
            }
            else
            {
                tone = "";
            }

            return String.Format("<span class=\"hl-badge{0}\">{1}</span>", tone, level);
        }

        private static string StateLed(Account account)
        {
            var html = new StringBuilder();

            if (account.Banned)
            {
                Led(html, "red", true, false, "Banned");
            }
            else if (AccountAdmin.OnlineCharacters(account).Count > 0)
            {
                Led(html, "green", true, true, "Playing");
            }
            else
            {
                Led(html, "green", true, false, "Active");
            }

            return html.ToString();
        }

        private static string Encode(string text)
        {
            return HttpUtility.HtmlEncode(text ?? String.Empty);
        }

        private static string Url(string text)
        {
            return HttpUtility.UrlEncode(text ?? String.Empty);
        }

        #endregion
    }
}
