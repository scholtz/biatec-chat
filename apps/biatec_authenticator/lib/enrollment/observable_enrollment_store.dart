import 'package:flutter/foundation.dart';

import 'enrollment_state.dart';
import 'enrollment_store.dart';

/// An [EnrollmentStore] that tells listeners when the stored enrollment changed, so the app notices at once when this device signs itself out (cancelling its own enrollment in Devices) instead of at the next tab change.
///
/// The enrollment lives in memory, which is the one authority: it is read from the disk once, and from then on every read answers from memory, every write and clear changes memory first (and tells the listeners), and the disk follows behind. So a keystore that fails or hangs can neither undo a sign-out, nor lose an enrollment the server already has, nor hold up the next write: one persisting step runs at a time, it starts again when something changed while it ran, and after a failure it is tried again when the app next asks (at most once per [saveRetryEvery]).
class ObservableEnrollmentStore extends ChangeNotifier
    implements EnrollmentStore {
  ObservableEnrollmentStore(this._inner, {DateTime Function()? clock})
      : _clock = clock ?? DateTime.now;

  final EnrollmentStore _inner;
  final DateTime Function() _clock;

  /// How many times the enrollment was deliberately cleared. A record that is merely missing is not a clear.
  int get clearCount => _clearCount;
  int _clearCount = 0;

  /// How often a read tries again to persist a change that did not reach the disk; reads are frequent (the pending list polls).
  static const saveRetryEvery = Duration(seconds: 30);

  /// The enrollment, or null when signed out or never enrolled. Meaningful once [_known].
  EnrollmentRecord? _record;

  /// Whether [_record] is the truth: it was loaded from the disk, or written or cleared since.
  bool _known = false;

  /// Counts changes made here; a persisting step or a load that ran across a change knows its answer is stale.
  int _version = 0;

  /// The disk does not hold [_record] yet.
  bool _dirty = false;
  Future<void>? _persisting;
  DateTime? _lastTry;
  Future<EnrollmentRecord?>? _loading;

  @override
  Future<EnrollmentRecord?> read() async {
    if (_known) {
      _persistBehind();
      return _record;
    }
    return _load();
  }

  /// One load at a time. A write or clear that happens meanwhile wins over what the disk said, also when the disk read fails.
  Future<EnrollmentRecord?> _load() {
    final running = _loading;
    if (running != null) return running.then((_) => _answer());
    final version = _version;
    final result = () async {
      try {
        final stored = await _inner.read();
        if (version == _version && !_known) {
          _record = stored;
          _known = true;
        }
      } on Object {
        // Changed here meanwhile: the answer is known. Otherwise nothing is, and the error is the caller's.
        if (version == _version && !_known) rethrow;
      } finally {
        _loading = null;
      }
      return _answer();
    }();
    _loading = result;
    return result;
  }

  EnrollmentRecord? _answer() => _record;

  /// The disk takes the latest state. The same step for a write and for a clear, so it is written once.
  Future<void> _persist() async {
    final version = _version;
    final record = _record;
    try {
      if (record == null) {
        await _inner.clear();
      } else {
        await _inner.write(record);
      }
      if (version == _version) _dirty = false;
    } on Object {
      // Stays dirty: the app works from memory and the next read tries again.
    }
  }

  void _persistBehind({bool force = false}) {
    if (!_dirty || _persisting != null) return;
    final last = _lastTry;
    if (!force && last != null && _clock().difference(last) < saveRetryEvery) {
      return;
    }
    _lastTry = _clock();
    final version = _version;
    _persisting = _persist().whenComplete(() {
      _persisting = null;
      // Changed while it ran: the newest state goes next, at once.
      if (_dirty && version != _version) _persistBehind(force: true);
    });
  }

  /// Memory first, then the disk behind: it completes at once and never waits for, or fails with, the storage.
  @override
  Future<void> write(EnrollmentRecord record) async {
    _change(record);
    notifyListeners();
  }

  /// Announced at once, before the storage is cleared and even when that fails: the device was signed out on purpose, and the app must stop using it now. Reads from here on answer "not enrolled".
  @override
  Future<void> clear() async {
    _clearCount++;
    _change(null);
    notifyListeners();
  }

  void _change(EnrollmentRecord? record) {
    _record = record;
    _known = true;
    _version++;
    _dirty = true;
    _persistBehind(force: true);
  }

  /// Completes when what was asked for so far has reached the disk, or the step failed (it then stays to be retried). For tests and for shutdown.
  Future<void> flush() async {
    while (_persisting != null) {
      await _persisting;
    }
  }

  /// For tests: drops the memory, as a restart does, so the next read goes to the disk.
  @visibleForTesting
  Future<void> forgetMemory() async {
    await flush();
    _known = false;
  }

  /// Whether a change is still waiting for the disk.
  bool get unsaved => _dirty;

  @override
  Future<String?> readAlias() => _inner.readAlias();

  @override
  Future<void> writeAlias(String alias) => _inner.writeAlias(alias);

  @override
  Future<void> clearAlias() => _inner.clearAlias();
}
