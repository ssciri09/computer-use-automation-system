using System.ComponentModel;

namespace FirstCore.Desktop;

/// <summary>
/// FirstCore Banking Platform — thick-client build 4.2.1.
///
/// A deliberately hostile stand-in for the *desktop* flavour of the same
/// vendor product the web mock emulates: the same fee-waiver workflow, the
/// same account matrix, the same runtime conditions (host congestion, closed
/// account, restricted party, no records) — but reached through a native
/// window tree instead of a DOM.
///
/// The obstacles that matter for automation are preserved:
///   - sign-on and a mandatory disclaimer gate the workspace
///   - the module pane does not exist until the nav item is chosen, and is
///     built ~1.2s later (the desktop analogue of a late-injected iframe)
///   - every host call shows a busy indicator that must *disappear* before
///     the result can be trusted; the indicator is added/removed from the
///     tree, not merely hidden
///   - the security interception is painted on the ROOT form, outside the
///     module pane — an agent scoped to the module sees nothing change
///
/// Everything is fabricated. No real data, no network, no persistence.
/// </summary>
internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}

internal sealed record Account(
    string Name, string Status, string Branch, string Product,
    string Balance, string FeeId, string FeeType, string FeeAmount, string FeeDate);

internal static class Bank
{
    public const string User = "operator";
    public const string Password = "letmein";
    public const string OverridePin = "7391";

    /// <summary>LATENCY_SCALE=0.2 runs the scenarios 5x faster while iterating.</summary>
    public static readonly double Scale =
        double.TryParse(Environment.GetEnvironmentVariable("LATENCY_SCALE"), out var s) ? s : 1.0;

    public static int Ms(double seconds) => (int)Math.Max(1, seconds * 1000 * Scale);

    public static readonly Dictionary<string, Account> Accounts = new()
    {
        ["12345"] = new("M R HOLLOWAY", "ACTIVE", "014", "REG CHECKING", "1,284.09",
                        "FEE-88213", "OVERDRAFT ITEM FEE", "35.00", "08/26/2026"),
        ["55555"] = new("D P ANANTHAKRISHNAN", "ACTIVE", "022", "PREMIER CHECKING", "412.55",
                        "FEE-88401", "OVERDRAFT ITEM FEE", "35.00", "08/25/2026"),
        ["99999"] = new("T L OKONKWO", "CLOSED", "007", "REG CHECKING", "0.00",
                        "FEE-71190", "OVERDRAFT ITEM FEE", "35.00", "07/02/2026"),
        ["00000"] = new("RESTRICTED PARTY", "ACTIVE", "001", "REG CHECKING", "9,430.11",
                        "FEE-90002", "OVERDRAFT ITEM FEE", "35.00", "08/26/2026"),
    };

    /// <summary>Per-account attempt counter: 55555 congests the host link twice, then succeeds.</summary>
    private static readonly Dictionary<string, int> Attempts = [];

    public static bool HostCongested(string key, string acct)
    {
        if (acct != "55555") return false;
        var k = $"{key}:{acct}";
        Attempts[k] = Attempts.GetValueOrDefault(k) + 1;
        return Attempts[k] < 3;
    }
}

internal sealed class MainForm : Form
{
    // Panes. AccessibleName becomes the UIA Name, which is what artifact
    // frame paths address — the desktop analogue of a frame name.
    private readonly Panel _logonPane = NewPane("LogonPane");
    private readonly Panel _disclaimerPane = NewPane("DisclaimerPane");
    private readonly Panel _navigationPane = NewPane("NavigationPane");
    private readonly Panel _workspacePane = NewPane("WorkspacePane");
    private Panel? _feeModulePane;
    private Panel? _securityOverlayPane;

    private TextBox _acctBox = null!;
    private Label? _spinner;
    private Label? _resultLabel;
    private Button? _waiveButton;
    private string _currentAccount = "";

    public MainForm()
    {
        Text = "FirstCore Banking Platform 4.2.1";
        AccessibleName = "FirstCore Banking Platform";
        Size = new Size(940, 640);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(208, 220, 237);
        Font = new Font("Tahoma", 8.25f);

        BuildLogon();
        BuildDisclaimer();
        BuildWorkspaceShell();

        Controls.Add(_logonPane);
        Controls.Add(_disclaimerPane);
        Controls.Add(_navigationPane);
        Controls.Add(_workspacePane);

        ShowOnly(_logonPane);
    }

    // ----------------------------------------------------------- sign-on

    private void BuildLogon()
    {
        _logonPane.Bounds = new Rectangle(250, 140, 420, 210);
        _logonPane.BackColor = Color.White;
        _logonPane.BorderStyle = BorderStyle.FixedSingle;

        _logonPane.Controls.Add(Header("Operator Sign On", 8, 8, 400));

        _logonPane.Controls.Add(NewLabel("lblUserId", "User ID:", 20, 60, 70));
        var user = new TextBox { Name = "txtUserId", AccessibleName = "User ID", Location = new Point(110, 57), Width = 200 };
        _logonPane.Controls.Add(user);

        _logonPane.Controls.Add(NewLabel("lblPassword", "Password:", 20, 95, 70));
        var pass = new TextBox
        {
            Name = "txtPassword", AccessibleName = "Password", UseSystemPasswordChar = true,
            Location = new Point(110, 92), Width = 200,
        };
        _logonPane.Controls.Add(pass);

        var error = NewLabel("lblLogonError", "", 20, 160, 380);
        error.ForeColor = Color.Firebrick;
        _logonPane.Controls.Add(error);

        var signOn = NewButton("btnSignOn", "Sign On", 110, 125);
        signOn.Click += (_, _) =>
        {
            if (user.Text == Bank.User && pass.Text == Bank.Password)
            {
                error.Text = "";
                error.AccessibleName = "";
                ShowOnly(_disclaimerPane);
            }
            else
            {
                error.Text = "Sign-on failed. Verify user id and password.";
                error.AccessibleName = error.Text;
            }
        };
        _logonPane.Controls.Add(signOn);

        _logonPane.Controls.Add(NewLabel("lblHint", "Test credentials: operator / letmein", 20, 185, 380));
    }

    // -------------------------------------------------------- disclaimer

    private void BuildDisclaimer()
    {
        _disclaimerPane.Bounds = new Rectangle(250, 160, 420, 190);
        _disclaimerPane.BackColor = Color.White;
        _disclaimerPane.BorderStyle = BorderStyle.FixedSingle;

        _disclaimerPane.Controls.Add(Header("Notice to Authorized Users", 8, 8, 400));
        _disclaimerPane.Controls.Add(NewLabel("lblNotice",
            "This system is the property of the institution and is for authorized\n" +
            "business use only. All activity is monitored and recorded.", 20, 50, 380, height: 60));

        var ack = NewButton("btnAcknowledge", "I Acknowledge", 20, 130, width: 120);
        ack.Click += (_, _) =>
        {
            ShowOnly(_navigationPane, _workspacePane);
        };
        _disclaimerPane.Controls.Add(ack);
    }

    // ---------------------------------------------------- workspace shell

    private void BuildWorkspaceShell()
    {
        _navigationPane.Bounds = new Rectangle(0, 0, 215, 601);
        _navigationPane.BackColor = Color.FromArgb(223, 232, 246);
        _navigationPane.BorderStyle = BorderStyle.FixedSingle;
        _navigationPane.Controls.Add(Header("Application Menu", 4, 4, 200));

        // Decoys: real modules with a different input contract, so a misrouted
        // agent lands somewhere plausible instead of on a dead control.
        _navigationPane.Controls.Add(NavItem("btnBalanceInquiry", "Balance Inquiry", 40, OpenStubModule));
        _navigationPane.Controls.Add(NavItem("btnTransactionHistory", "Transaction History", 70, OpenStubModule));
        _navigationPane.Controls.Add(NavItem("btnFeeManagement", "Fee Management", 100, _ => OpenFeeModule()));
        _navigationPane.Controls.Add(NavItem("btnAddressMaintenance", "Address Maintenance", 130, OpenStubModule));

        _workspacePane.Bounds = new Rectangle(215, 0, 709, 601);
        _workspacePane.BackColor = Color.FromArgb(208, 220, 237);
        _workspacePane.BorderStyle = BorderStyle.FixedSingle;
        _workspacePane.Controls.Add(Header("Workspace", 6, 6, 300));
        _workspacePane.Controls.Add(NewLabel("lblNoModule", "Select a function from the application menu.", 12, 40, 400));
    }

    private void OpenStubModule(string moduleName)
    {
        ClearModule();
        var pane = NewPane("StubModulePane");
        pane.Bounds = new Rectangle(8, 60, 690, 520);
        pane.BackColor = Color.White;
        pane.BorderStyle = BorderStyle.FixedSingle;
        pane.Controls.Add(Header(moduleName, 8, 8, 400));
        pane.Controls.Add(NewLabel("lblStub",
            $"{moduleName} is not available in this build.", 12, 44, 500));
        _feeModulePane = pane;
        _workspacePane.Controls.Add(pane);
    }

    // -------------------------------------------------------- fee module

    private void OpenFeeModule()
    {
        ClearModule();

        // The module pane does not exist yet — it is built after a delay, the
        // desktop analogue of a script-injected iframe. An agent must wait for
        // it rather than assume it is present after the nav click.
        var loading = NewLabel("lblModuleLoading", "Loading module...", 12, 44, 300);
        _workspacePane.Controls.Add(loading);

        var timer = new System.Windows.Forms.Timer { Interval = Bank.Ms(1.2) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            timer.Dispose();
            _workspacePane.Controls.Remove(loading);
            loading.Dispose();
            BuildFeeModule();
        };
        timer.Start();
    }

    private void BuildFeeModule()
    {
        var pane = NewPane("FeeModulePane");
        pane.Bounds = new Rectangle(8, 40, 690, 540);
        pane.BackColor = Color.White;
        pane.BorderStyle = BorderStyle.FixedSingle;

        pane.Controls.Add(Header("Fee Management — Account Inquiry", 8, 8, 500));
        pane.Controls.Add(NewLabel("lblAcctPrompt", "Account Identifier:", 12, 46, 110));

        _acctBox = new TextBox
        {
            Name = "txtAccountId", AccessibleName = "Account Identifier",
            Location = new Point(130, 43), Width = 140, MaxLength = 12,
        };
        pane.Controls.Add(_acctBox);

        var inquiry = NewButton("btnExecuteInquiry", "Execute Inquiry", 290, 41, width: 110);
        inquiry.Click += (_, _) => RunInquiry(pane);
        pane.Controls.Add(inquiry);

        _feeModulePane = pane;
        _workspacePane.Controls.Add(pane);
    }

    private void RunInquiry(Panel pane)
    {
        ClearResults(pane);
        var acct = _acctBox.Text.Trim();
        _currentAccount = acct;

        WithSpinner(pane, Bank.Ms(2.6), () =>
        {
            if (Bank.HostCongested("feeinq", acct))
            {
                AddMessage(pane, "lblHostBusy",
                    "HOST-0521: Legacy host link congested. Resubmit inquiry.", Color.DarkGoldenrod);
                return;
            }

            if (!Bank.Accounts.TryGetValue(acct, out var record))
            {
                AddMessage(pane, "lblNoRecords",
                    "No records found for the supplied account identifier.", Color.DarkGoldenrod);
                return;
            }

            var detail = NewLabel("lblAccountDetail",
                $"ACCT {acct}   NAME {record.Name}   BR {record.Branch}   " +
                $"PRODUCT {record.Product}   STATUS {record.Status}   LEDGER BAL {record.Balance}",
                12, 90, 660);
            pane.Controls.Add(detail);

            var fee = NewLabel("lblFeeRow",
                $"FEE REF {record.FeeId}   {record.FeeType}   POSTED {record.FeeDate}   AMT {record.FeeAmount}",
                12, 130, 660);
            pane.Controls.Add(fee);

            _waiveButton = NewButton("btnWaiveFee", "Waive Fee", 12, 165, width: 100);
            _waiveButton.Click += (_, _) => RunWaive(pane, acct, record);
            pane.Controls.Add(_waiveButton);
        });
    }

    private void RunWaive(Panel pane, string acct, Account record)
    {
        if (_resultLabel is not null)
        {
            pane.Controls.Remove(_resultLabel);
            _resultLabel.Dispose();
            _resultLabel = null;
        }

        WithSpinner(pane, Bank.Ms(2.0), () =>
        {
            if (record.Status == "CLOSED")
            {
                AddResult(pane, "lblWaiveError",
                    "Error: Account is closed. Fee waiver not permitted.", Color.Firebrick);
                return;
            }

            if (acct == "00000")
            {
                // Painted on the ROOT form, deliberately outside the module
                // pane: an agent scoped to FeeModulePane observes no change.
                ShowSecurityOverlay(pane, acct, record);
                return;
            }

            var confirmation = "RVSL-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
            AddResult(pane, "lblWaiveResult",
                $"Fee {record.FeeId} reversed. Credit of {record.FeeAmount} posted to account {acct}. " +
                $"Confirmation {confirmation}", Color.DarkGreen);
        });
    }

    // --------------------------------------------------- security overlay

    private void ShowSecurityOverlay(Panel modulePane, string acct, Account record)
    {
        var overlay = NewPane("SecurityOverlayPane");
        overlay.Bounds = new Rectangle(270, 190, 400, 210);
        overlay.BackColor = Color.White;
        overlay.BorderStyle = BorderStyle.Fixed3D;

        overlay.Controls.Add(Header("SECURITY INTERCEPTION: Manager Override PIN Required", 8, 8, 380));
        overlay.Controls.Add(NewLabel("lblSecurityNotice",
            $"Account {acct} carries a supervisory restriction. A manager\noverride PIN is required to continue this transaction.",
            12, 46, 370, height: 40));
        overlay.Controls.Add(NewLabel("lblPinPrompt", "Override PIN:", 12, 100, 90));

        var pin = new TextBox
        {
            Name = "txtOverridePin", AccessibleName = "Override PIN",
            Location = new Point(110, 97), Width = 80, MaxLength = 4, UseSystemPasswordChar = true,
        };
        overlay.Controls.Add(pin);

        var error = NewLabel("lblOverrideError", "", 12, 130, 370);
        error.ForeColor = Color.Firebrick;
        overlay.Controls.Add(error);

        var authorize = NewButton("btnAuthorize", "Authorize", 110, 160, width: 100);
        authorize.Click += (_, _) =>
        {
            if (pin.Text != Bank.OverridePin)
            {
                error.Text = "Override PIN rejected.";
                error.AccessibleName = error.Text;
                return;
            }

            Controls.Remove(overlay);
            overlay.Dispose();
            _securityOverlayPane = null;

            var confirmation = "RVSL-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
            AddResult(modulePane, "lblWaiveResult",
                $"Override accepted. Fee {record.FeeId} reversed on account {acct}. " +
                $"Confirmation {confirmation}", Color.DarkGreen);
        };
        overlay.Controls.Add(authorize);

        _securityOverlayPane = overlay;
        Controls.Add(overlay);
        overlay.BringToFront();
    }

    // ------------------------------------------------------------ helpers

    /// <summary>
    /// Shows a busy indicator, runs the work after the delay, then removes the
    /// indicator from the tree entirely — so "wait until absent" is a real
    /// signal rather than a visibility flag an agent could misread.
    /// </summary>
    private void WithSpinner(Panel pane, int delayMs, Action work)
    {
        _spinner = NewLabel("lblHostBusyIndicator", "Legacy host communication in progress...", 12, 200, 400);
        _spinner.ForeColor = Color.FromArgb(21, 66, 139);
        pane.Controls.Add(_spinner);

        var timer = new System.Windows.Forms.Timer { Interval = delayMs };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            timer.Dispose();
            if (_spinner is not null)
            {
                pane.Controls.Remove(_spinner);
                _spinner.Dispose();
                _spinner = null;
            }
            work();
        };
        timer.Start();
    }

    private void ClearModule()
    {
        foreach (var c in _workspacePane.Controls.OfType<Control>().ToList())
        {
            if (c is Panel || c.Name == "lblModuleLoading")
            {
                _workspacePane.Controls.Remove(c);
                c.Dispose();
            }
        }
        _feeModulePane = null;
        _waiveButton = null;
        _resultLabel = null;
    }

    private void ClearResults(Panel pane)
    {
        foreach (var name in new[]
                 {
                     "lblAccountDetail", "lblFeeRow", "btnWaiveFee", "lblWaiveResult",
                     "lblWaiveError", "lblNoRecords", "lblHostBusy",
                 })
        {
            foreach (var c in pane.Controls.Find(name, searchAllChildren: false))
            {
                pane.Controls.Remove(c);
                c.Dispose();
            }
        }
        _waiveButton = null;
        _resultLabel = null;
    }

    private void AddMessage(Panel pane, string name, string text, Color color)
    {
        var label = NewLabel(name, text, 12, 90, 660);
        label.ForeColor = color;
        pane.Controls.Add(label);
    }

    private void AddResult(Panel pane, string name, string text, Color color)
    {
        _resultLabel = NewLabel(name, text, 12, 210, 660, height: 40);
        _resultLabel.ForeColor = color;
        pane.Controls.Add(_resultLabel);
    }

    private void ShowOnly(params Panel[] visible)
    {
        foreach (var pane in new[] { _logonPane, _disclaimerPane, _navigationPane, _workspacePane })
            pane.Visible = visible.Contains(pane);
    }

    private static Panel NewPane(string accessibleName) => new()
    {
        Name = accessibleName,
        AccessibleName = accessibleName,
        AccessibleRole = AccessibleRole.Pane,
    };

    private static Label Header(string text, int x, int y, int width) => new()
    {
        Name = "lblHeader_" + text.Split(' ')[0],
        AccessibleName = text,
        Text = text,
        Location = new Point(x, y),
        Width = width,
        Height = 20,
        BackColor = Color.FromArgb(203, 220, 243),
        ForeColor = Color.FromArgb(21, 66, 139),
        Font = new Font("Tahoma", 8.25f, FontStyle.Bold),
        TextAlign = ContentAlignment.MiddleLeft,
    };

    private static Label NewLabel(string name, string text, int x, int y, int width, int height = 20) => new()
    {
        Name = name,
        AccessibleName = text,
        Text = text,
        Location = new Point(x, y),
        Width = width,
        Height = height,
    };

    private static Button NewButton(string name, string text, int x, int y, int width = 90) => new()
    {
        Name = name,
        AccessibleName = text,
        Text = text,
        Location = new Point(x, y),
        Width = width,
        Height = 24,
    };

    private static Control NavItem(string name, string text, int y, Action<string> onClick)
    {
        var button = NewButton(name, text, 8, y, width: 190);
        button.TextAlign = ContentAlignment.MiddleLeft;
        button.FlatStyle = FlatStyle.Flat;
        button.BackColor = Color.FromArgb(223, 232, 246);
        button.Click += (_, _) => onClick(text);
        return button;
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _securityOverlayPane?.Dispose();
        base.OnFormClosed(e);
    }
}
