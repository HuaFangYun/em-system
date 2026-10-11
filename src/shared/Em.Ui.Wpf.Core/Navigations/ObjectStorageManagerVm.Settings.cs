using System.Collections.ObjectModel;
using System.Globalization;
using Em.Api.Core.Models;
using Em.Ui.Wpf.Shared;
using Em.Shared;

namespace Em.Ui.Wpf.Navigations;

/// <summary>A volume offered in the policy editor, with whether the policy uses it.</summary>
public sealed class StorePolicyVolumeChoice : NotifyPropertyBase
{
   /// <summary>Creates a choice.</summary>
   public StorePolicyVolumeChoice(StoreVolumeInfo volume, bool isSelected) {
      Volume = volume;
      IsSelected = isSelected;
   }

   /// <summary>The volume.</summary>
   public StoreVolumeInfo Volume { get; }

   /// <summary>The policy places blobs on this volume.</summary>
   public bool IsSelected { get => Get<bool>(); set => Set(value); }
}

public sealed partial class ObjectStorageManagerVm
{
   private string? _selectedVolumeId;
   private string? _selectedPolicyId;

   #region Volumes

   /// <summary>Volumes.</summary>
   public ObservableCollection<StoreVolumeInfo> Volumes { get; } = [];

   /// <summary>Selected volume row.</summary>
   public StoreVolumeInfo? SelectedVolume {
      get => Get<StoreVolumeInfo>();
      set => Set(value, _ => { _selectedVolumeId = value?.Id; UpdateState(); });
   }

   /// <summary>The volume editor is open.</summary>
   public bool IsVolumeEditorOpen { get => Get<bool>(); private set => Set(value, _ => UpdateState()); }

   /// <summary>Volume being edited.</summary>
   public StoreVolumeSave VolumeDraft { get => Get<StoreVolumeSave>() ?? new(); private set => Set(value); }

   /// <summary>The volume being edited already holds copies, so its path is fixed.</summary>
   public bool IsVolumePathLocked { get => Get<bool>(); private set => Set(value); }

   /// <summary>Result of the last folder check in the editor.</summary>
   public string VolumeValidation { get => Get<string>() ?? ""; private set => Set(value); }

   /// <summary>Volume states with captions.</summary>
   public StoreOption<StoreVolumeState>[] VolumeStates { get; } = [
      new(StoreVolumeState.Active, "Active"), new(StoreVolumeState.ReadOnly, "Read-only"), new(StoreVolumeState.Offline, "Offline")
   ];

   #endregion

   #region Policies

   /// <summary>Policies.</summary>
   public ObservableCollection<StorePolicyInfo> Policies { get; } = [];

   /// <summary>Selected policy row.</summary>
   public StorePolicyInfo? SelectedPolicy {
      get => Get<StorePolicyInfo>();
      set => Set(value, _ => { _selectedPolicyId = value?.Id; UpdateState(); });
   }

   /// <summary>The policy editor is open.</summary>
   public bool IsPolicyEditorOpen { get => Get<bool>(); private set => Set(value, _ => UpdateState()); }

   private string? _policyEditId;

   /// <summary>Policy name.</summary>
   public string PolicyName { get => Get<string>() ?? ""; set => Set(value); }

   /// <summary>Policy state.</summary>
   public StorePolicyState PolicyState { get => Get<StorePolicyState>(); set => Set(value); }

   /// <summary>Public access.</summary>
   public StoreAccess PolicyAccess { get => Get<StoreAccess>(); set => Set(value); }

   /// <summary>Copies written before an upload succeeds.</summary>
   public int PolicyReplicaMin { get => Get<int>(); set => Set(value); }

   /// <summary>Copies background replication aims for.</summary>
   public int PolicyReplicaTarget { get => Get<int>(); set => Set(value); }

   /// <summary>Non-current versions kept per object; empty keeps all.</summary>
   public string PolicyKeepVersions { get => Get<string>() ?? ""; set => Set(value); }

   /// <summary>Maximum age of non-current versions in days; empty keeps them forever.</summary>
   public string PolicyKeepDays { get => Get<string>() ?? ""; set => Set(value); }

   /// <summary>Byte quota per bucket in MB; empty is unlimited.</summary>
   public string PolicyQuotaMb { get => Get<string>() ?? ""; set => Set(value); }

   /// <summary>Object quota per bucket; empty is unlimited.</summary>
   public string PolicyQuotaObjects { get => Get<string>() ?? ""; set => Set(value); }

   /// <summary>Maximum object size in MB; empty is unlimited.</summary>
   public string PolicyMaxObjectMb { get => Get<string>() ?? ""; set => Set(value); }

   /// <summary>Optional note.</summary>
   public string? PolicyNote { get => Get<string>(); set => Set(value); }

   /// <summary>Every volume, the policy's own first in priority order.</summary>
   public ObservableCollection<StorePolicyVolumeChoice> PolicyVolumes { get; } = [];

   /// <summary>Selected volume in the policy editor.</summary>
   public StorePolicyVolumeChoice? SelectedPolicyVolume { get => Get<StorePolicyVolumeChoice>(); set => Set(value, _ => UpdateState()); }

   /// <summary>Policy states with captions.</summary>
   public StoreOption<StorePolicyState>[] PolicyStates { get; } = [new(StorePolicyState.Active, "Active"), new(StorePolicyState.Disabled, "Disabled")];

   /// <summary>Access options with captions.</summary>
   public StoreOption<StoreAccess>[] AccessOptions { get; } = [new(StoreAccess.Private, "Private"), new(StoreAccess.PublicRead, "Public read (/store)")];

   #endregion

   private void RegisterVolumeCommands() {
      RegisterCommand(nameof(NewVolumeCommand), NewVolumeCommand, () => CanBrowse);
      RegisterCommand(nameof(EditVolumeCommand), EditVolumeCommand, () => CanBrowse && SelectedVolume is not null);
      RegisterCommand(nameof(DeleteVolumeCommand), DeleteVolumeCommand, () => CanBrowse && SelectedVolume is not null);
      RegisterCommand(nameof(ValidateVolumeCommand), ValidateVolumeCommand, () => CanEdit && IsVolumeEditorOpen);
      RegisterCommand(nameof(SaveVolumeCommand), SaveVolumeCommand, () => CanEdit && IsVolumeEditorOpen);
   }

   private void RegisterPolicyCommands() {
      RegisterCommand(nameof(NewPolicyCommand), NewPolicyCommand, () => CanBrowse);
      RegisterCommand(nameof(EditPolicyCommand), EditPolicyCommand, () => CanBrowse && SelectedPolicy is not null);
      RegisterCommand(nameof(DeletePolicyCommand), DeletePolicyCommand, () => CanBrowse && SelectedPolicy is not null);
      RegisterCommand(nameof(SavePolicyCommand), SavePolicyCommand, () => CanEdit && IsPolicyEditorOpen);
      RegisterCommand(nameof(MoveVolumeUpCommand), MoveVolumeUpCommand, () => CanEdit && SelectedPolicyVolume is { } v && PolicyVolumes.IndexOf(v) > 0);
      RegisterCommand(nameof(MoveVolumeDownCommand), MoveVolumeDownCommand,
         () => CanEdit && SelectedPolicyVolume is { } v && PolicyVolumes.IndexOf(v) is var i && i >= 0 && i < PolicyVolumes.Count - 1);
   }

   #region Volume commands

   /// <summary>Opens the editor for a new volume.</summary>
   public void NewVolumeCommand() {
      if (!CanBrowse) return;
      VolumeDraft = new StoreVolumeSave { Path = "./data/objects" };
      IsVolumePathLocked = false;
      VolumeValidation = "";
      IsVolumeEditorOpen = true;
   }

   /// <summary>Opens the editor for the selected volume.</summary>
   public void EditVolumeCommand() {
      if (!CanBrowse || SelectedVolume is not { } v) return;
      VolumeDraft = new StoreVolumeSave { Id = v.Id, Name = v.Name, State = v.State, Type = v.Type, Path = v.Path, Note = v.Note };
      IsVolumePathLocked = v.Blobs + v.Missing > 0;
      VolumeValidation = v.Probe.Message;
      IsVolumeEditorOpen = true;
   }

   /// <summary>Checks the folder of the volume draft.</summary>
   public Task ValidateVolumeCommand() => RunAsync("Checking folder...", async () => {
      var result = await Service.PostGetMeta_StoreVolumeValidate(VolumeDraft);
      VolumeValidation = result.IsValid
         ? $"{result.Message} {result.ResolvedPath}" + (result.FreeBytes is { } free ? $" · {FormatSize(free)} free" : "")
         : result.Message;
   });

   /// <summary>Saves the volume editor.</summary>
   public Task SaveVolumeCommand() => RunAsync("Saving volume...", async () => {
      var saved = await Service.PostGetMeta_StoreVolumeSave(VolumeDraft);
      IsVolumeEditorOpen = false;
      _selectedVolumeId = saved.Id;
      await LoadSettingsAsync();
      Status = $"Volume '{saved.Name}' saved.";
   });

   /// <summary>Deletes the selected volume after confirmation.</summary>
   public async Task DeleteVolumeCommand() {
      if (SelectedVolume is not { } v) return;
      if (Confirm?.Invoke($"Delete volume '{v.Name}'? Only a volume without copies and outside every policy can be deleted. Files in its folder are not touched.", "Delete volume") != true) return;
      await RunAsync("Deleting volume...", async () => {
         await Service.PostMeta_StoreVolumeDelete(v.Id);
         await LoadSettingsAsync();
         Status = $"Volume '{v.Name}' deleted.";
      });
   }

   #endregion

   #region Policy commands

   /// <summary>Opens the editor for a new policy.</summary>
   public void NewPolicyCommand() {
      if (!CanBrowse) return;
      _policyEditId = null;
      PolicyName = "";
      PolicyState = StorePolicyState.Active;
      PolicyAccess = StoreAccess.Private;
      PolicyReplicaMin = PolicyReplicaTarget = 1;
      PolicyKeepVersions = PolicyKeepDays = PolicyQuotaMb = PolicyQuotaObjects = PolicyMaxObjectMb = "";
      PolicyNote = null;
      FillPolicyVolumes([]);
      IsPolicyEditorOpen = true;
   }

   /// <summary>Opens the editor for the selected policy.</summary>
   public void EditPolicyCommand() {
      if (!CanBrowse || SelectedPolicy is not { } p) return;
      _policyEditId = p.Id;
      PolicyName = p.Name;
      PolicyState = p.State;
      PolicyAccess = p.Access;
      PolicyReplicaMin = p.ReplicaMin;
      PolicyReplicaTarget = p.ReplicaTarget;
      PolicyKeepVersions = p.KeepVersions?.ToString(CultureInfo.InvariantCulture) ?? "";
      PolicyKeepDays = p.KeepDays?.ToString(CultureInfo.InvariantCulture) ?? "";
      PolicyQuotaMb = ToMb(p.QuotaBytes);
      PolicyQuotaObjects = p.QuotaObjects?.ToString(CultureInfo.InvariantCulture) ?? "";
      PolicyMaxObjectMb = ToMb(p.MaxObjectBytes);
      PolicyNote = p.Note;
      FillPolicyVolumes(p.VolumeIds);
      IsPolicyEditorOpen = true;
   }

   private void FillPolicyVolumes(string[] selectedIds) {
      PolicyVolumes.Clear();
      foreach (var id in selectedIds)
         if (Volumes.FirstOrDefault(v => v.Id == id) is { } v) PolicyVolumes.Add(new StorePolicyVolumeChoice(v, true));
      foreach (var v in Volumes.Where(v => !selectedIds.Contains(v.Id))) PolicyVolumes.Add(new StorePolicyVolumeChoice(v, false));
      SelectedPolicyVolume = null;
   }

   /// <summary>Moves the selected volume one place up in priority.</summary>
   public void MoveVolumeUpCommand() => MoveVolume(-1);

   /// <summary>Moves the selected volume one place down in priority.</summary>
   public void MoveVolumeDownCommand() => MoveVolume(1);

   private void MoveVolume(int delta) {
      if (SelectedPolicyVolume is not { } choice) return;
      var index = PolicyVolumes.IndexOf(choice);
      var target = index + delta;
      if (index < 0 || target < 0 || target >= PolicyVolumes.Count) return;
      PolicyVolumes.Move(index, target);
      SelectedPolicyVolume = choice;
      UpdateState();
   }

   /// <summary>Builds the save request from the editor fields; throws <see cref="Em.Shared.ActionException"/> 400 for unreadable numbers.</summary>
   public StorePolicySave BuildPolicyDraft() => new() {
      Id = _policyEditId, Name = PolicyName.Trim(), State = PolicyState, Access = PolicyAccess, Note = PolicyNote,
      ReplicaMin = PolicyReplicaMin, ReplicaTarget = PolicyReplicaTarget,
      KeepVersions = ParseOptionalInt(PolicyKeepVersions, "Kept versions"), KeepDays = ParseOptionalInt(PolicyKeepDays, "Kept days"),
      QuotaBytes = FromMb(PolicyQuotaMb, "Byte quota"), QuotaObjects = ParseOptional(PolicyQuotaObjects, "Object quota"),
      MaxObjectBytes = FromMb(PolicyMaxObjectMb, "Maximum object size"),
      VolumeIds = PolicyVolumes.Where(v => v.IsSelected).Select(v => v.Volume.Id).ToArray()
   };

   /// <summary>Saves the policy editor.</summary>
   public Task SavePolicyCommand() => RunAsync("Saving policy...", async () => {
      var saved = await Service.PostGetMeta_StorePolicySave(BuildPolicyDraft());
      IsPolicyEditorOpen = false;
      _selectedPolicyId = saved.Id;
      await LoadSettingsAsync();
      await LoadBucketsAsync();
      Status = $"Policy '{saved.Name}' saved.";
   });

   /// <summary>Deletes the selected policy after confirmation.</summary>
   public async Task DeletePolicyCommand() {
      if (SelectedPolicy is not { } p) return;
      if (Confirm?.Invoke($"Delete policy '{p.Name}'? Only a policy without buckets can be deleted.", "Delete policy") != true) return;
      await RunAsync("Deleting policy...", async () => {
         await Service.PostMeta_StorePolicyDelete(p.Id);
         await LoadSettingsAsync();
         Status = $"Policy '{p.Name}' deleted.";
      });
   }

   private static long? ParseOptional(string text, string field) {
      text = text.Trim();
      if (text.Length == 0) return null;
      if (long.TryParse(text, NumberStyles.Integer, CultureInfo.CurrentCulture, out var value) && value >= 0) return value;
      throw new Em.Shared.ActionException($"{field} must be a whole number or empty.", 400);
   }

   private static int? ParseOptionalInt(string text, string field) =>
      ParseOptional(text, field) is { } value ? value <= int.MaxValue ? (int)value : throw new Em.Shared.ActionException($"{field} is too large.", 400) : null;

   private static long? FromMb(string text, string field) {
      text = text.Trim();
      if (text.Length == 0) return null;
      if (decimal.TryParse(text, NumberStyles.Number, CultureInfo.CurrentCulture, out var mb) && mb >= 0 && mb <= 1_000_000_000m)
         return (long)Math.Round(mb * 1024m * 1024m);
      throw new Em.Shared.ActionException($"{field} must be a number of MB or empty.", 400);
   }

   private static string ToMb(long? bytes) =>
      bytes is { } b ? (b / 1024m / 1024m).ToString("0.###", CultureInfo.CurrentCulture) : "";

   /// <summary>Retention caption of a policy for the table.</summary>
   public static string RetentionText(StorePolicyInfo p) => (p.KeepVersions, p.KeepDays) switch {
      (null, null) => "Keep all",
      ({ } v, null) => $"{v} old versions",
      (null, { } d) => $"{d} days",
      ({ } v, { } d) => $"{v} old versions, {d} days"
   };

   #endregion
}
