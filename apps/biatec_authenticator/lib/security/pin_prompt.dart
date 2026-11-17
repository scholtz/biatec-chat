import 'dart:async';
import 'dart:ui';

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';

import '../theme.dart';

/// The PIN of the app, used by a Windows build on a computer where Windows Hello cannot be used (T-250, docs/authenticator-windows-key-store.md).
///
/// The app does not check the PIN and does not keep it: it goes with the one native call that needs it, and the computer's TPM checks it (it is the usage secret of keys in the TPM, which locks itself after too many wrong tries). This file only asks the user for it, on the app's own screen.

/// The limits of a new PIN. The native side holds the same ones (key_channels.cpp).
const pinMinLength = 6;
const pinMaxLength = 64;

/// Asks the user for the PIN: a new one, typed twice ([create]), or the existing one, with [reason] saying what it is for. Null when the user closed the dialog.
typedef PinPromptHandler = Future<String?> Function({required bool create, required String reason});

/// What shows the PIN dialog. The app sets it (main.dart). Null where there is no screen to show it on (a test, a background isolate): a call that needs the PIN is then refused, never guessed.
PinPromptHandler? pinPromptHandler;

/// The PIN from the user, or null when there is nobody to ask, the dialog was closed, or it failed.
Future<String?> askPin({required bool create, String reason = ''}) async {
  final handler = pinPromptHandler;
  if (handler == null) return null;
  try {
    return await handler(create: create, reason: reason);
  } on Object {
    return null;
  }
}

/// How long the dialog for the existing PIN stays open. Shorter than the two minutes the lock waits for a prompt (`LockController.promptTimeout`), so the dialog is gone before the lock gives up on it. A new PIN has no limit: the user reads and types twice.
const pinEnterTimeout = Duration(seconds: 90);

bool _open = false;

/// Shows the PIN dialog over everything [navigator] shows. One at a time: a second request while one is open is answered null.
Future<String?> showPinDialog(NavigatorState navigator, {required bool create, required String reason}) async {
  final overlay = navigator.overlay;
  if (_open || overlay == null) return null;
  _open = true;
  final route = DialogRoute<String>(
    context: overlay.context,
    barrierDismissible: false,
    builder: (_) => PinDialog(create: create, reason: reason),
  );
  Timer? timeout;
  if (!create) {
    timeout = Timer(pinEnterTimeout, () {
      if (route.isActive) navigator.removeRoute(route);
    });
  }
  try {
    return await navigator.push(route);
  } finally {
    timeout?.cancel();
    _open = false;
  }
}

/// The texts of the PIN dialog: English and Slovak, by the language of the computer (as the enrollment texts).
class PinStrings {
  const PinStrings._(this._t);

  factory PinStrings.of([Locale? locale]) =>
      (locale ?? PlatformDispatcher.instance.locale).languageCode == 'sk' ? sk : en;

  final Map<String, String> _t;

  Iterable<String> get keys => _t.keys;
  String text(String key) => _t[key]!;

  static const en = PinStrings._({
    'createTitle': 'Choose a PIN for this app',
    'createText':
        'Windows Hello cannot be used on this computer, so this app asks for its own PIN when it opens and for every approval. The security chip (TPM) of this computer guards it and locks for a while after too many wrong tries.',
    'createWarning': 'The PIN cannot be recovered. If you forget it, this computer has to be enrolled again.',
    'enterTitle': 'Enter the PIN of this app',
    'pin': 'PIN',
    'newPin': 'New PIN (at least 6 characters)',
    'repeat': 'The same PIN again',
    'tooShort': 'The PIN needs at least 6 characters.',
    'different': 'The two PINs are not the same.',
    'show': 'Show the PIN',
    'hide': 'Hide the PIN',
    'cancel': 'Cancel',
    'confirm': 'Confirm',
    'save': 'Set the PIN',
  });

  static const sk = PinStrings._({
    'createTitle': 'Zvoľte si PIN pre túto aplikáciu',
    'createText':
        'Windows Hello sa na tomto počítači nedá použiť, preto si táto aplikácia pri otvorení a pri každom schválení pýta vlastný PIN. Stráži ho bezpečnostný čip (TPM) tohto počítača a po priveľa nesprávnych pokusoch sa na čas zamkne.',
    'createWarning': 'PIN sa nedá obnoviť. Ak ho zabudnete, tento počítač treba zaregistrovať znova.',
    'enterTitle': 'Zadajte PIN tejto aplikácie',
    'pin': 'PIN',
    'newPin': 'Nový PIN (najmenej 6 znakov)',
    'repeat': 'Ten istý PIN ešte raz',
    'tooShort': 'PIN musí mať najmenej 6 znakov.',
    'different': 'Zadané PIN kódy sa nezhodujú.',
    'show': 'Zobraziť PIN',
    'hide': 'Skryť PIN',
    'cancel': 'Zrušiť',
    'confirm': 'Potvrdiť',
    'save': 'Nastaviť PIN',
  });
}

/// The dialog itself. Pops the PIN, or null on Cancel.
class PinDialog extends StatefulWidget {
  const PinDialog({super.key, required this.create, required this.reason, this.strings});

  final bool create;

  /// What the existing PIN is asked for (an approval, the unlock).
  final String reason;

  /// The texts; the computer's language when null.
  final PinStrings? strings;

  @override
  State<PinDialog> createState() => _PinDialogState();
}

class _PinDialogState extends State<PinDialog> {
  final _pin = TextEditingController();
  final _repeat = TextEditingController();
  final _repeatFocus = FocusNode();
  bool _hidden = true;
  String? _error;

  late final PinStrings _s = widget.strings ?? PinStrings.of();

  @override
  void dispose() {
    _pin.dispose();
    _repeat.dispose();
    _repeatFocus.dispose();
    super.dispose();
  }

  void _submit() {
    final pin = _pin.text;
    if (widget.create) {
      // Only a new PIN is held to the rules. An existing one is whatever the TPM accepts.
      final problem = pin.length < pinMinLength
          ? _s.text('tooShort')
          : pin != _repeat.text
              ? _s.text('different')
              : null;
      if (problem != null) {
        setState(() => _error = problem);
        return;
      }
    } else if (pin.isEmpty) {
      return;
    }
    Navigator.of(context).pop(pin);
  }

  Widget _field(TextEditingController controller, String label, Key key,
          {bool first = false, FocusNode? focus, VoidCallback? onDone}) =>
      TextField(
        key: key,
        controller: controller,
        focusNode: focus,
        autofocus: first,
        obscureText: _hidden,
        enableSuggestions: false,
        autocorrect: false,
        enableIMEPersonalizedLearning: false,
        keyboardType: TextInputType.visiblePassword,
        inputFormatters: [LengthLimitingTextInputFormatter(pinMaxLength)],
        textInputAction: onDone == null ? TextInputAction.next : TextInputAction.done,
        onSubmitted: (_) => onDone == null ? _repeatFocus.requestFocus() : onDone(),
        onChanged: (_) {
          if (_error != null) setState(() => _error = null);
        },
        decoration: InputDecoration(
          labelText: label,
          border: const OutlineInputBorder(),
          suffixIcon: first
              ? IconButton(
                  key: const Key('pin-show'),
                  tooltip: _s.text(_hidden ? 'show' : 'hide'),
                  icon: Icon(_hidden ? Icons.visibility_outlined : Icons.visibility_off_outlined),
                  onPressed: () => setState(() => _hidden = !_hidden),
                )
              : null,
        ),
      );

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final create = widget.create;
    return AlertDialog(
      key: const Key('pin-dialog'),
      icon: Icon(create ? Icons.pin_outlined : Icons.lock_outline, color: theme.colorScheme.primary),
      title: Text(_s.text(create ? 'createTitle' : 'enterTitle')),
      content: ConstrainedBox(
        constraints: const BoxConstraints(maxWidth: 420),
        child: SingleChildScrollView(
          child: Column(
            mainAxisSize: MainAxisSize.min,
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              if (create) ...[
                Text(_s.text('createText')),
                const SizedBox(height: AppSpacing.s),
                Text(_s.text('createWarning'),
                    key: const Key('pin-warning'),
                    style: theme.textTheme.bodyMedium?.copyWith(fontWeight: FontWeight.w600)),
              ] else if (widget.reason.isNotEmpty)
                Text(widget.reason, key: const Key('pin-reason')),
              const SizedBox(height: AppSpacing.m),
              _field(_pin, _s.text(create ? 'newPin' : 'pin'), const Key('pin-field'),
                  first: true, onDone: create ? null : _submit),
              if (create) ...[
                const SizedBox(height: AppSpacing.s),
                _field(_repeat, _s.text('repeat'), const Key('pin-repeat'),
                    focus: _repeatFocus, onDone: _submit),
              ],
              if (_error != null) ...[
                const SizedBox(height: AppSpacing.s),
                Semantics(
                  liveRegion: true,
                  child: Text(_error!,
                      key: const Key('pin-error'), style: TextStyle(color: theme.colorScheme.error)),
                ),
              ],
            ],
          ),
        ),
      ),
      actions: [
        TextButton(
            key: const Key('pin-cancel'),
            onPressed: () => Navigator.of(context).pop(),
            child: Text(_s.text('cancel'))),
        FilledButton(
            key: const Key('pin-ok'), onPressed: _submit, child: Text(_s.text(create ? 'save' : 'confirm'))),
      ],
    );
  }
}
