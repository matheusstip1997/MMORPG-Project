#nullable enable
using System;
using System.Collections.Generic;

namespace Game.UI.ViewModels
{
    /// <summary>
    /// Character creation placeholder (R-9.10-06): a name and an initial class from <c>CharacterCreationConfig</c>.
    /// The name rules are checked by the server (spec 9.10); the appearance is Open and sent as the empty version 0
    /// blob. <see cref="RequestId"/> is the idempotency key: new each time the screen opens, kept for retries, so a
    /// create whose answer was lost returns the same character when sent again.
    /// </summary>
    public sealed class CharacterCreateViewModel : ViewModel
    {
        private string[] _classIds = Array.Empty<string>();

        public string Name { get; private set; } = "";

        /// <summary>Input length cap for the text field (the server enforces the real rules).</summary>
        public int NameMaxLength { get; private set; }

        public IReadOnlyList<string> ClassIds => _classIds;

        public int SelectedClass { get; private set; } = -1;

        public string? SelectedClassId => SelectedClass >= 0 && SelectedClass < _classIds.Length ? _classIds[SelectedClass] : null;

        public Guid RequestId { get; private set; }

        public LoginStatus Status { get; private set; } = LoginStatus.None;

        public bool CanCreate => Name.Length > 0 && SelectedClassId != null && !Status.IsBusy;

        public void Open(string[] classIds, int nameMaxLength, Guid requestId)
        {
            _classIds = classIds;
            NameMaxLength = nameMaxLength;
            Name = "";
            SelectedClass = classIds.Length > 0 ? 0 : -1;
            RequestId = requestId;
            Status = LoginStatus.None;
            NotifyChanged();
        }

        public void SetName(string name)
        {
            if (name == Name || Status.IsBusy)
                return;
            Name = name;
            NotifyChanged();
        }

        public void SelectClass(int index)
        {
            if (index == SelectedClass || index < 0 || index >= _classIds.Length || Status.IsBusy)
                return;
            SelectedClass = index;
            NotifyChanged();
        }

        public void SetStatus(LoginStatus status)
        {
            Status = status;
            NotifyChanged();
        }
    }
}
