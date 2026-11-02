def rw(p, f):
    b = open(p, 'rb').read().decode('utf-8')
    crlf = '\r\n' in b
    s = f(b.replace('\r\n', '\n'))
    open(p, 'wb').write((s.replace('\n', '\r\n') if crlf else s).encode('utf-8'))


def lc(s):
    s = s.replace("import '../security/secure_storage.dart';", "import '../integrity/integrity.dart';\nimport '../integrity/integrity_checker.dart';\nimport '../security/secure_storage.dart';", 1)
    s = s.replace("    this.idleCheckEvery = const Duration(seconds: 5),\n  })", "    this.idleCheckEvery = const Duration(seconds: 5),\n    this.integrity,\n    this.failClosed = true,\n  })", 1)
    s = s.replace("  final Duration idleCheckEvery;\n", """  final Duration idleCheckEvery;

  /// The safety check of the phone and the app. Null only where there is none to run (a screen in a test).
  final IntegrityGuard? integrity;

  /// True: a failed check keeps the secure area closed and says so. The build decides (a release build fails closed; a debug build only warns, so the app can be developed on an emulator or with a debugger). The default is closed.
  final bool failClosed;
""", 1)
    s = s.replace("  bool _disposed = false;\n\n  /// Counts unlock", """  bool _disposed = false;

  Set<IntegrityReason> _warning = const {};

  /// What a debug build found wrong with the phone, shown as a warning (a release build never gets here: it is closed instead). Empty when healthy.
  Set<IntegrityReason> get integrityWarning => _warning;

  bool _compromisedByIntegrity = false;

  /// True when the closed state came from the safety check, so checking again can end it (any other cause stays).
  bool get canRecheckIntegrity =>
      _state == AppLockState.compromised && _compromisedByIntegrity;

  /// Counts unlock""", 1)
    s = s.replace("    _set(AppLockState.unlocking);\n    final attempt = ++_attempt;\n", "    _set(AppLockState.unlocking);\n    final attempt = ++_attempt;\n    if (!await _integrityAllows()) return;\n    if (_disposed || attempt != _attempt) return;\n", 1)
    s = s.replace("    if (_state != AppLockState.unlocked) return false;\n    _reauthenticating++;", "    if (_state != AppLockState.unlocked) return false;\n    // The check is run again before every approval, not only at start.\n    if (!await _integrityAllows(force: true)) return false;\n    if (_state != AppLockState.unlocked) return false;\n    _reauthenticating++;", 1)
    s = s.replace("    final at = _backgroundAt;\n    _backgroundAt = null;\n    if (at == null", "    // Coming back is when a tool attached in the meantime shows.\n    _integrityAllows(force: true);\n    final at = _backgroundAt;\n    _backgroundAt = null;\n    if (at == null", 1)
    s = s.replace("  void markCompromised() {\n    _clearMemory();", "  void markCompromised() {\n    _compromisedByIntegrity = false;\n    _clearMemory();", 1)
    s = s.replace("  /// A fresh biometric for one action", """  /// Runs the safety check (a cached answer unless [force]). False when the secure area must stay closed: the app then goes to the compromised screen. In a build that only warns the answer is true and the problem is kept in [integrityWarning].
  Future<bool> _integrityAllows({bool force = false}) async {
    final guard = integrity;
    if (guard == null) return true;
    final result = await guard.current(force: force);
    if (_disposed) return true;
    if (!result.compromised) {
      if (_warning.isNotEmpty) {
        _warning = const {};
        _notify();
      }
      return true;
    }
    if (!failClosed) {
      _warning = result.reasons;
      _notify();
      return true;
    }
    _clearMemory();
    _compromisedByIntegrity = true;
    _set(AppLockState.compromised);
    return false;
  }

  /// Checks the phone again from the compromised screen. If it passes now (a debugger was detached), the app asks for the fingerprint as at any start.
  Future<void> recheckIntegrity() async {
    if (!canRecheckIntegrity) return;
    final guard = integrity;
    if (guard == null) return;
    final result = await guard.current(force: true);
    if (_disposed || result.compromised || !canRecheckIntegrity) return;
    _compromisedByIntegrity = false;
    _set(AppLockState.locked);
    await unlock();
  }

  /// A fresh biometric for one action""", 1)
    return s


rw('lib/lock/lock_controller.dart', lc)


def ls(s):
    s = s.replace("class CompromisedScreen extends StatelessWidget {\n  const CompromisedScreen({super.key});\n", "class CompromisedScreen extends StatelessWidget {\n  const CompromisedScreen({super.key, required this.lock});\n\n  final LockController lock;\n", 1)
    a = s.index("              const Text('The app found that this phone")
    b = s.index("            ],", a)
    s = s[:a] + """              const Text('This phone, or this copy of the app, did not pass a '
                  'safety check, so the app will not open your codes or '
                  'approvals here. If you did not change anything, install '
                  'the app again from the official store, or use another '
                  'phone.'),
              if (lock.canRecheckIntegrity) ...[
                const SizedBox(height: AppSpacing.l),
                OutlinedButton.icon(
                  key: const Key('integrity-recheck'),
                  onPressed: lock.recheckIntegrity,
                  icon: const Icon(Icons.refresh),
                  label: const Text('Check again'),
                ),
              ],
""" + s[b:]
    s = s.replace("                  if (problem != null) ...[", "                  if (lock.integrityWarning.isNotEmpty) ...[\n                    const SizedBox(height: AppSpacing.m),\n                    const StatusBanner(\n                      key: Key('integrity-warning'),\n                      severity: StatusSeverity.warning,\n                      title: 'This phone failed a safety check',\n                      message: 'This is a development build, so it opens anyway. '\n                          'A release build would stay closed.',\n                    ),\n                  ],\n                  if (problem != null) ...[", 1)
    return s


rw('lib/lock/lock_screens.dart', ls)
rw('lib/shell/app_lock_gate.dart', lambda s: s.replace("const CompromisedScreen()", "CompromisedScreen(lock: widget.lock)"))
