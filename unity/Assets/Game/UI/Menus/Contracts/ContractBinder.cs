#nullable enable
using System;
using Game.Gameplay.Entities;
using Game.UI.Config;
using Game.UI.ViewModels.Contracts;
using Game.UI.ViewModels.Hud;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation.Contracts;
using UnityEngine.InputSystem;

namespace Game.UI.Menus.Contracts
{
    /// <summary>What the player does in the C window (WP-M4-10 part B); the binder turns it into requests.</summary>
    public interface IContractWindowActions
    {
        void CloseWindow();
        void SelectTab(ContractTab tab);
        void CancelContract(long contractId);
        /// <summary>The crafter's portfolio of an own contract (the customer reads who works for it).</summary>
        void ViewCrafter(long contractId);
        void ClosePortfolio();
        void StepDefinition(int delta);
        void SwitchSupplier(int input);
        void CycleCrafterItem(int input);
        void OpenPicker(int input);
        void PickRow(int row);
        void ClearSlot(int input);
        void SetGold(string text);
        void SetDeadline(string text);
        void Propose();
        /// <summary>A text field gained or lost the focus: gameplay keys are off while it has it.</summary>
        void FocusField(bool focused);
    }

    /// <summary>What the player does at the contract board (WP-M4-10 part B).</summary>
    public interface IContractBoardActions
    {
        void CloseBoard();
        void SelectRow(int index);
        void Accept();
        void StepJob();
        void StepKind();
        void NextPage();
        void FirstPage();
        /// <summary>The portfolio of the target-locked player.</summary>
        void ViewTarget();
        void ClosePortfolio();
    }

    /// <summary>
    /// Connects the C window and the contract board (spec 5.4, 9.3 C, 9.13; M4 decisions 10-12, 14; WP-M4-10 part B) to the world
    /// client: the own contracts (<see cref="WorldClientFeed.ContractListReceived"/>, the C badge) go into the
    /// <see cref="ContractWindowViewModel"/>, the board's pages into the <see cref="ContractBoardViewModel"/>; the buttons send
    /// through <see cref="WorldClientFeed.ContractRequests"/>, one action at a time per window, answered after the commit. A refusal
    /// shows its <c>ReasonCodeStrings</c> text in the HUD toast, an applied answer its <c>UiStrings</c> toast. The board opens from the
    /// board's yes and closes when it leaves reach; the C window from its key (<c>toggleContracts</c>) or the C button, anywhere.
    /// Both wait for level 20 (<see cref="Unlocked"/>, the <c>UnlockSchedule</c> row; client only). Left-edge windows: closed while a
    /// trade is open. The server decides everything.
    /// </summary>
    public sealed class ContractBinder : IContractWindowActions, IContractBoardActions, IDisposable
    {
        private readonly ContractConfig _config;
        private readonly ContractWindowViewModel _window;
        private readonly ContractBoardViewModel _board;
        private readonly ToastViewModel _toast;
        private readonly int _toastMs;
        private readonly Func<double> _now;
        private readonly InputAction? _toggle;
        private WorldClientFeed? _feed;
        private bool _fieldFocused;

        /// <param name="readToggleKey">False in tests: no Input System action is made for the toggle key.</param>
        public ContractBinder(ContractConfig config, ContractWindowViewModel window, ContractBoardViewModel board, ToastViewModel toast,
            int toastMs, Func<double> nowMs, bool readToggleKey = true)
        {
            _config = config;
            _window = window;
            _board = board;
            _toast = toast;
            _toastMs = toastMs;
            _now = nowMs;
            window.Form.NormalBag = config.Inventory.NormalBagCode;
            if (readToggleKey && config.ToggleBinding.Length > 0)
            {
                _toggle = new InputAction("ToggleContracts", InputActionType.Button, config.ToggleBinding);
                _toggle.Enable();
            }
        }

        public WorldClientFeed? Feed => _feed;

        /// <summary>Whether the left-edge windows are blocked (a trade is open; the HUD's <c>WindowGroup</c>); null: never.</summary>
        public Func<bool>? TradeOpen { get; set; }

        /// <summary>Whether contracts are unlocked at the own level (M4 decision 14; the HUD's <c>UnlockSchedule</c>); null: always.</summary>
        public Func<bool>? Unlocked { get; set; }

        /// <summary>The level contracts unlock at, for the locked toast.</summary>
        public int UnlockLevel { get; set; }

        private bool TradeIsOpen => TradeOpen?.Invoke() ?? false;

        private bool IsUnlocked => Unlocked?.Invoke() ?? true;

        public void Bind(WorldClientFeed feed)
        {
            Unbind();
            _feed = feed;
            feed.ContractListReceived += OnList;
            feed.ContractBoardReceived += OnBoard;
            feed.ContractAnswered += OnAnswered;
            feed.PortfolioReceived += OnPortfolio;
            feed.OutpostInteracted += OnInteracted;
            feed.InventoryReceived += OnContents;
            feed.Disconnected += OnDisconnected;
            if (feed.Contracts != null) // it may have come before the HUD bound
                OnList(feed.Contracts);
            if (feed.Inventory != null)
                OnContents(feed.Inventory);
        }

        public void Unbind()
        {
            if (_feed == null)
                return;
            _feed.ContractListReceived -= OnList;
            _feed.ContractBoardReceived -= OnBoard;
            _feed.ContractAnswered -= OnAnswered;
            _feed.PortfolioReceived -= OnPortfolio;
            _feed.OutpostInteracted -= OnInteracted;
            _feed.InventoryReceived -= OnContents;
            _feed.Disconnected -= OnDisconnected;
            if (_fieldFocused)
                _feed.WindowTextInput = false;
            _fieldFocused = false;
            _feed = null;
        }

        public void Dispose()
        {
            Unbind();
            _toggle?.Dispose();
        }

        /// <summary>The C button and the key: opens or closes the window; nothing while a trade is open or below the level.</summary>
        public void Toggle()
        {
            if (_window.Open)
            {
                SetOpen(false);
                return;
            }
            if (!TradeIsOpen && IsUnlocked)
                SetOpen(true);
        }

        /// <summary>
        /// Once per frame: a trade keeps both windows closed, the board closes when its NPC leaves reach, and the toggle key
        /// (ignored while a text field has focus).
        /// </summary>
        public void Frame(double nowMs)
        {
            if (TradeIsOpen)
            {
                if (_window.Open)
                    SetOpen(false);
                CloseBoard();
            }
            if (_board.Open && _feed != null && _feed.NpcTarget != _board.NpcEntityId)
                CloseBoard();
            if (_toggle != null && _toggle.WasPressedThisFrame() && !(_feed?.TextInputActive ?? false))
                Toggle();
        }

        // ---- The C window ----

        public void CloseWindow() => SetOpen(false);

        public void SelectTab(ContractTab tab)
        {
            _window.SetTab(tab);
            if (tab == ContractTab.Propose && _feed?.Inventory != null)
                OnContents(_feed.Inventory);
        }

        public void CancelContract(long contractId)
        {
            if (_window.PendingAction != 0 || _window.Entry(contractId) == null)
                return;
            uint id = _feed?.ContractRequests?.CancelContract(contractId) ?? 0;
            if (id != 0)
                _window.ActionSent(id, contractId);
        }

        public void ViewCrafter(long contractId)
        {
            uint id = _feed?.ContractRequests?.RequestPortfolio(new PortfolioRequest { Action = PortfolioAction.View, ContractId = contractId }) ?? 0;
            if (id != 0)
                _window.PortfolioAsked(id);
        }

        void IContractWindowActions.ClosePortfolio() => _window.ClosePortfolio();

        public void StepDefinition(int delta)
        {
            _window.Form.Step(delta);
            _window.Touch();
        }

        public void SwitchSupplier(int input)
        {
            _window.Form.Switch(input);
            _window.Touch();
        }

        public void CycleCrafterItem(int input)
        {
            _window.Form.CycleCrafterItem(input);
            _window.Touch();
        }

        public void OpenPicker(int input)
        {
            var form = _window.Form;
            form.PickerTarget = form.PickerTarget == input ? -1 : input;
            _window.Touch();
        }

        /// <summary>Row <paramref name="row"/> of the open picker, as the presenter listed it (<see cref="ContractFormModel.Candidates"/>).</summary>
        public void PickRow(int row)
        {
            var form = _window.Form;
            int input = form.PickerTarget;
            var rows = form.Candidates(input);
            if (row < 0 || row >= rows.Count)
                return;
            if (form.Assign(input, rows[row]) is { } refused)
                Toast(refused);
            _window.Touch();
        }

        public void ClearSlot(int input)
        {
            _window.Form.ClearSlot(input);
            _window.Touch();
        }

        public void SetGold(string text)
        {
            _window.Form.Gold = long.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture,
                out long gold) ? gold : 0;
            _window.Touch();
        }

        public void SetDeadline(string text)
        {
            _window.Form.DeadlineHours = int.TryParse(text, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out int hours) ? hours : 0;
            _window.Touch();
        }

        /// <summary>Sends the form when its checks pass (the button is off otherwise); one action at a time.</summary>
        public void Propose()
        {
            var form = _window.Form;
            if (_window.PendingAction != 0 || form.ProposeKind == ForgeKind.None || form.ProposeDefNetId == 0)
                return;
            var reason = form.Check();
            if (reason != ContractReason.None)
            {
                Toast(reason.ToString());
                return;
            }
            uint id = _feed?.ContractRequests?.Propose(form.ProposeKind, form.ProposeDefNetId, form.Gold, form.DeadlineHours, form.ToSlots()) ?? 0;
            if (id != 0)
                _window.ActionSent(id);
        }

        public void FocusField(bool focused)
        {
            _fieldFocused = focused && _window.Open;
            if (_feed != null)
                _feed.WindowTextInput = _fieldFocused;
        }

        private void SetOpen(bool open)
        {
            if (!open)
                FocusField(false);
            _window.SetOpen(open);
            if (open && _feed?.Inventory != null)
                OnContents(_feed.Inventory);
        }

        // ---- The board ----

        public void CloseBoard() => _board.Close();

        public void SelectRow(int index) => _board.Select(index);

        public void Accept()
        {
            if (_board.PendingAccept != 0 || _board.SelectedRow is not { } row)
                return;
            uint id = _feed?.ContractRequests?.Accept(_board.NpcEntityId, row.ContractId) ?? 0;
            if (id != 0)
                _board.AcceptSent(id);
        }

        public void StepJob()
        {
            _board.StepJob(_config.Jobs);
            RequestBoard();
        }

        public void StepKind()
        {
            _board.StepKind();
            RequestBoard();
        }

        public void NextPage()
        {
            if (!_board.HasMore || _board.PendingBoard != 0) // the page asked for decides whether there is a next one
                return;
            _board.NextPage();
            RequestBoard();
        }

        public void FirstPage()
        {
            _board.FirstPage();
            RequestBoard();
        }

        public void ViewTarget()
        {
            uint target = _feed?.LockedTargetId ?? 0;
            if (target == 0)
            {
                Toast(ContractReason.PortfolioNotFound.ToString());
                return;
            }
            uint id = _feed?.ContractRequests?.RequestPortfolio(new PortfolioRequest { Action = PortfolioAction.View, TargetEntityId = target }) ?? 0;
            if (id != 0)
                _board.PortfolioAsked(id);
        }

        void IContractBoardActions.ClosePortfolio() => _board.ClosePortfolio();

        private void RequestBoard()
        {
            if (!_board.Open)
                return;
            uint id = _feed?.ContractRequests?.RequestBoard(_board.NpcEntityId, _board.JobFilter, _board.KindFilter, _board.After) ?? 0;
            if (id != 0)
                _board.BoardSent(id);
        }

        // ---- The feed ----

        private void OnInteracted(OutpostInteractResult result)
        {
            if (result.Kind != NpcInteraction.Contracts || result.Reason != OutpostReason.None || TradeIsOpen)
                return;
            if (!IsUnlocked)
            {
                _toast.ShowText(ContractTextIds.Locked, "level", NumberText.Of(UnlockLevel), _now(), _toastMs);
                return;
            }
            _board.OpenFor(result.NpcEntityId);
            RequestBoard();
        }

        private void OnList(ContractList list)
        {
            if (list.Reason != ContractReason.None)
            {
                if (list.RequestId != 0)
                    Toast(list.Reason.ToString());
                return;
            }
            _window.SetList(list);
        }

        private void OnBoard(ContractBoard board)
        {
            if (_board.OnPage(board) && board.Reason != ContractReason.None)
                Toast(board.Reason.ToString());
        }

        private void OnAnswered(ContractResult result)
        {
            bool mine = _window.Answered(result);
            bool accept = !mine && _board.Answered(result);
            if (!mine && !accept)
                return;
            if (result.Reason != ContractReason.None)
            {
                Toast(result.Reason.ToString());
                if (accept)
                    RequestBoard(); // the row may be gone or changed: show the board as it is now
                return;
            }
            switch (result.Action)
            {
                case ContractAction.Propose:
                    _toast.ShowText(ContractTextIds.ToastProposed, "gold", NumberText.Of(result.Gold), _now(), _toastMs);
                    _window.Form.Step(0); // the chosen stacks went into escrow: the form starts again on the same action
                    _window.Touch();
                    break;
                case ContractAction.Accept:
                    _toast.ShowText(ContractTextIds.ToastAccepted, "", "", _now(), _toastMs);
                    RequestBoard();
                    break;
                case ContractAction.Cancel:
                    if (result.Cost > 0)
                        _toast.ShowText(ContractTextIds.ToastCancelCost, "gold", NumberText.Of(result.Cost), _now(), _toastMs);
                    else
                        _toast.ShowText(ContractTextIds.ToastCancelled, "", "", _now(), _toastMs);
                    break;
            }
        }

        private void OnPortfolio(PortfolioState state)
        {
            if (_window.PortfolioAnswer(state) || _board.PortfolioAnswer(state))
            {
                if (state.Reason != ContractReason.None)
                    Toast(state.Reason.ToString());
            }
        }

        private void OnContents(InventoryContents contents)
        {
            _window.Form.SetBag(contents.Slots, SafeArea(contents)); // WP-M4-10 part C: the enchant action's items
            _window.Touch();
        }

        /// <summary>The Safe bag's equipment area as last sent (an enchant contract's items; escrow never takes equipped gear).</summary>
        private InventorySlotEntry[] SafeArea(InventoryContents contents)
        {
            byte safe = _config.Enchant?.SafeCode ?? 0;
            foreach (var container in contents.Containers)
            {
                if (safe != 0 && container.Code == safe)
                    return container.Slots;
            }
            return Array.Empty<InventorySlotEntry>();
        }

        private void OnDisconnected(string reasonCode)
        {
            FocusField(false);
            _window.Clear();
            _board.Clear();
        }

        private void Toast(string reasonCode) => _toast.Show(reasonCode, false, _now(), _toastMs);

        /// <summary>
        /// The cost a cancel of <paramref name="entry"/> would charge the player now (the crafter after work started), 0 when free;
        /// -1 when the player may not cancel it (<see cref="ContractMath.CheckCancel"/>, the server's rule).
        /// </summary>
        public static long CancelCost(ContractEntry entry, int costPercent)
        {
            if (ContractMath.CheckCancel(entry.Status, entry.WorkStarted, entry.Role, out bool costs) != ContractReason.None)
                return -1;
            return costs ? ContractMath.CancelCost(entry.Gold, costPercent) : 0;
        }
    }
}
