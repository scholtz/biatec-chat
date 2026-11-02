import 'dart:math';

import 'package:flutter/foundation.dart';

import '../totp/core/otp_account.dart';
import '../totp/vault/totp_vault.dart';
import '../totp/vault/vault_key_store.dart';
import 'codes_services.dart';

enum CodesPhase { loading, ready, failed }

/// One account in the list, with its secret held in memory while the app is unlocked.
class CodeEntry {
  CodeEntry(this.info, this.account);

  VaultAccountInfo info;
  OtpAccount account;
}

/// Why an account was not added.
class AddRefused implements Exception {
  const AddRefused(this.message);
  final String message;

  @override
  String toString() => 'AddRefused: $message';
}

/// The list of two-factor accounts: loads them from the vault when the tab opens, keeps their secrets in memory only while the app is unlocked, and adds, renames, deletes and reorders them.
class CodesController extends ChangeNotifier {
  CodesController(this.services) {
    _removeHook = services.lock.addLockListener(wipe);
  }

  final CodesServices services;
  late final VoidCallback _removeHook;

  CodesPhase _phase = CodesPhase.loading;
  CodesPhase get phase => _phase;

  String? _problem;

  /// Why the list could not be opened, in words for the user.
  String? get problem => _problem;

  final List<CodeEntry> _entries = [];
  List<CodeEntry> get entries => List.unmodifiable(_entries);

  String _query = '';
  String get query => _query;
  set query(String value) {
    _query = value.trim();
    notifyListeners();
  }

  /// The entries matching the search (issuer or account name, any case), in list order.
  List<CodeEntry> get visible {
    if (_query.isEmpty) return entries;
    final q = _query.toLowerCase();
    return [
      for (final e in _entries)
        if (e.info.issuer.toLowerCase().contains(q) ||
            e.info.label.toLowerCase().contains(q))
          e
    ];
  }

  bool _tapToReveal = false;
  bool get tapToReveal => _tapToReveal;

  DateTime? _latestSeen;

  /// The latest time codes were shown at (see `OtpGenerator.clockIsPlausible`), or null.
  DateTime? get latestSeen => _latestSeen;

  bool _disposed = false;
  int _loads = 0;

  /// A time saved more than this far ahead of the clock was saved while the clock ran fast: it is not a floor, or a corrected clock would hide the codes until that date.
  static const aheadLimit = Duration(days: 30);

  /// Opens the vault and reads every account. Each secret read may ask for the fingerprint (the app's own unlock covers the first 30 seconds).
  Future<void> load() async {
    final load = ++_loads;
    _phase = CodesPhase.loading;
    _problem = null;
    _notify();
    try {
      _tapToReveal = await services.prefs.readTapToReveal();
      final seen = await services.prefs.readLatestSeen();
      final now = services.clock();
      // Saved while the clock ran ahead: forget it, so the corrected clock is trusted again.
      _latestSeen =
          seen != null && seen.isAfter(now.add(aheadLimit)) ? null : seen;
      final infos = await services.vault.list();
      final loaded = <CodeEntry>[];
      try {
        for (final info in infos) {
          loaded.add(CodeEntry(info, await services.vault.readSecret(info.id)));
        }
      } on Object {
        for (final e in loaded) {
          e.account.wipe();
        }
        rethrow;
      }
      if (_disposed || load != _loads) {
        for (final e in loaded) {
          e.account.wipe();
        }
        return;
      }
      _wipeEntries();
      _entries.addAll(loaded);
      _phase = CodesPhase.ready;
    } on VaultBiometricsChanged {
      services.lock.markBiometricsChanged();
      _fail(
          'Your fingerprints or face changed, so the codes cannot be opened.');
    } on VaultAuthFailed {
      _fail('The fingerprint or face check did not pass. Try again.');
    } on VaultRolledBack {
      _fail('The saved accounts are older than what this phone has seen, so '
          'they are not opened. Restore them from your backup file.');
    } on VaultCorrupt {
      _fail('The saved accounts are damaged, so they are not opened. '
          'Restore them from your backup file.');
    } on Object {
      _fail('The saved accounts could not be opened. Try again.');
    }
    _notify();
  }

  void _fail(String message) {
    _wipeEntries();
    _phase = CodesPhase.failed;
    _problem = message;
  }

  /// Notes that codes were shown at [now]; the highest time seen is kept.
  Future<void> noteShownAt(DateTime now) async {
    final seen = _latestSeen;
    if (seen != null && !now.isAfter(seen)) return;
    _latestSeen = now;
    try {
      await services.prefs.writeLatestSeen(now);
    } on Object {
      // Not saved: the check is weaker until the next time.
    }
  }

  Future<void> setTapToReveal(bool value) async {
    _tapToReveal = value;
    _notify();
    try {
      await services.prefs.writeTapToReveal(value);
    } on Object {
      // Holds until the app closes.
    }
  }

  /// Saves [account] (the vault seals it; a fingerprint may be asked). A copy of an account already in the list is refused. The caller wipes its own [account].
  Future<CodeEntry> add(OtpAccount account) async {
    for (final e in _entries) {
      if (account.isDuplicateOf(e.account)) {
        final who = e.info.issuer.isEmpty
            ? e.info.label
            : '${e.info.issuer}, ${e.info.label}';
        throw AddRefused('You already have this account: $who.');
      }
    }
    final info = await services.vault.add(account);
    final entry = CodeEntry(info, account.copyWith(id: info.id));
    _entries.add(entry);
    _phase = CodesPhase.ready;
    _notify();
    return entry;
  }

  /// Renames an account. Throws [OtpAccountException] with the reason when the name is not allowed.
  Future<void> rename(String id, {String? issuer, String? label}) async {
    final entry = _find(id);
    final info = await services.vault.update(id, issuer: issuer, label: label);
    final old = entry.account;
    entry
      ..info = info
      ..account = old.copyWith(issuer: info.issuer, label: info.label);
    old.wipe();
    _notify();
  }

  /// Deletes an account after a fresh fingerprint. False when the fingerprint was refused (nothing is deleted).
  Future<bool> delete(String id) async {
    final entry = _find(id);
    if (!await services.lock.reauthenticate('Delete this account')) {
      return false;
    }
    await services.vault.delete(id);
    entry.account.wipe();
    _entries.remove(entry);
    _notify();
    return true;
  }

  /// Moves the account at [from] to [to] in the full list (the order is kept in the vault).
  Future<void> reorder(int from, int to) async {
    if (from < 0 || from >= _entries.length) return;
    final target = max(0, min(to, _entries.length - 1));
    if (from == target) return;
    final moved = _entries.removeAt(from);
    _entries.insert(target, moved);
    _notify();
    await services.vault.reorder([for (final e in _entries) e.info.id]);
  }

  /// Moves a counter-based account to its next counter and shows that code.
  Future<void> nextCounter(String id) async {
    final entry = _find(id);
    final next = await services.vault.incrementCounter(id);
    final old = entry.account;
    entry
      ..info = (await services.vault.list()).firstWhere((i) => i.id == id)
      ..account = old.copyWith(counter: next);
    old.wipe();
    _notify();
  }

  CodeEntry _find(String id) => _entries.firstWhere((e) => e.info.id == id,
      orElse: () => throw ArgumentError('No such account.'));

  /// Clears every secret from memory and empties the list (the app locked).
  void wipe() {
    _wipeEntries();
    _phase = CodesPhase.loading;
    _notify();
  }

  void _wipeEntries() {
    for (final e in _entries) {
      e.account.wipe();
    }
    _entries.clear();
  }

  void _notify() {
    if (!_disposed) notifyListeners();
  }

  @override
  void dispose() {
    _disposed = true;
    _removeHook();
    _wipeEntries();
    super.dispose();
  }
}
