import 'package:flutter/material.dart';

import '../shell/tab_reload.dart';
import '../theme.dart';
import '../ui/ui.dart';
import 'settings_client.dart';
import 'settings_controller.dart';

/// Loads a [SettingsController] on open and wraps the three settings screens:
/// a loading state, a retryable unavailable state, and a status line for the
/// last step. Status is always icon plus text, never colour alone.
class SettingsFrame extends StatefulWidget {
  const SettingsFrame({
    super.key,
    required this.title,
    required this.services,
    required this.builder,
    this.footer,
    this.controller,
  });

  final String title;
  final SettingsServices services;

  /// Shared by the settings tabs of the shell, so the data is loaded once for all of them; the frame makes (and disposes) its own when null.
  final SettingsController? controller;

  /// The screen body once loaded.
  final List<Widget> Function(BuildContext, SettingsController) builder;

  /// Shown below the body whatever state it is in.
  final Widget? footer;

  @override
  State<SettingsFrame> createState() => _SettingsFrameState();
}

class _SettingsFrameState extends State<SettingsFrame> with TabReloadListener {
  late final SettingsController _controller;

  @override
  void initState() {
    super.initState();
    _controller = widget.controller ?? widget.services.controller();
    // A shared controller another tab has loaded already is used as it is.
    if (widget.services.enabled && _controller.phase == SettingsPhase.loading) {
      _controller.load();
    }
  }

  @override
  void onReload() {
    if (widget.services.enabled) _controller.load(quiet: true);
  }

  @override
  void dispose() {
    if (widget.controller == null) _controller.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final footer = widget.footer;
    return Scaffold(
      appBar: AppBar(title: Text(widget.title)),
      bottomNavigationBar:
          footer == null ? null : SafeArea(top: false, child: footer),
      body: !widget.services.enabled
          ? const EmptyState(
              key: Key('not-available'),
              icon: Icons.info_outline,
              title: 'Not available yet',
              message: 'This Biatec OIDC does not offer device and settings '
                  'management; it only runs against the mock for now.',
            )
          : ListenableBuilder(
              listenable: _controller,
              builder: (context, _) {
                final c = _controller;
                switch (c.phase) {
                  case SettingsPhase.loading:
                    return const LoadingSkeleton();
                  case SettingsPhase.unavailable:
                    return ErrorState(
                      key: const Key('unavailable'),
                      message: c.error ?? 'Not available.',
                      offline: c.offline,
                      onRetry: c.load,
                    );
                  case SettingsPhase.ready:
                    return RefreshIndicator(
                      onRefresh: () => c.load(quiet: true),
                      child: ListView(
                        children: [
                          ...settingsStatus(c),
                          ...widget.builder(context, c),
                        ],
                      ),
                    );
                }
              },
            ),
    );
  }
}

/// The progress bar, the problem and the notice of the last step, each with an icon and words. Shown above the content of every settings screen.
List<Widget> settingsStatus(SettingsController c, {bool showError = true}) => [
      if (c.busy) const LinearProgressIndicator(),
      if (c.error != null && showError)
        Padding(
          key: const Key('status-error'),
          padding: const EdgeInsets.all(AppSpacing.s),
          child: StatusBanner(
              severity: StatusSeverity.danger,
              title: 'Problem',
              message: c.error!),
        ),
      if (c.notice != null)
        Padding(
          key: const Key('status-notice'),
          padding: const EdgeInsets.all(AppSpacing.s),
          // Announced like a problem is: the user waits for it.
          child: Semantics(
            liveRegion: true,
            child: StatusBanner(
                severity: StatusSeverity.success,
                title: 'Done',
                message: c.notice!),
          ),
        ),
    ];

/// Applies [change] to the settings. A change that loosens protection is first confirmed in a sheet that says the phone will ask for a fingerprint or face; one that tightens applies at once.
Future<void> applyWithConfirm(
    BuildContext context,
    SettingsController controller,
    ConsentSettings account,
    Map<String, Object?> change,
    {required String title,
    required String message,
    required String confirmLabel}) async {
  if (isLoosening(account, change)) {
    final yes = await confirmSheet(
      context,
      title: title,
      message: message,
      confirmLabel: confirmLabel,
      cancelLabel: 'Keep as it is',
      biometric: true,
      icon: Icons.fingerprint,
    );
    if (!yes) return;
  }
  await controller.apply(change);
}

/// A decimal EUR amount as Biatec OIDC takes it (`250`, `250.50`).
final RegExp eurFormat = RegExp(r'^(0|[1-9][0-9]*)(\.[0-9]{1,2})?$');

/// Asks for a EUR amount in a sheet from the bottom of the screen. Null when cancelled.
///
/// The unit is written in the field, and an entry that is not an amount is explained under it before anything is sent. With [current], the sheet says as the user types whether the new amount raises or lowers it (raising needs approval on this device, lowering applies at once).
Future<String?> askEur(BuildContext context,
    {required String title,
    required String initial,
    required String hint,
    String? current}) {
  return showModalBottomSheet<String>(
    context: context,
    isScrollControlled: true,
    showDragHandle: true,
    builder: (_) =>
        _EurSheet(title: title, initial: initial, hint: hint, current: current),
  );
}

class _EurSheet extends StatefulWidget {
  const _EurSheet(
      {required this.title,
      required this.initial,
      required this.hint,
      this.current});

  final String title;
  final String initial;
  final String hint;
  final String? current;

  @override
  State<_EurSheet> createState() => _EurSheetState();
}

class _EurSheetState extends State<_EurSheet> {
  late final TextEditingController _text =
      TextEditingController(text: widget.initial);
  String? _problem;

  @override
  void dispose() {
    _text.dispose();
    super.dispose();
  }

  void _submit() {
    final v = _text.text.trim();
    if (!eurFormat.hasMatch(v)) {
      setState(() => _problem = 'Enter an amount like 250 or 250.50.');
      return;
    }
    Navigator.of(context).pop(v);
  }

  /// What the amount typed so far does to the current one, in words.
  String? _effect() {
    final current = double.tryParse(widget.current ?? '');
    final typed = _text.text.trim();
    if (current == null || !eurFormat.hasMatch(typed)) return null;
    final next = double.parse(typed);
    if (next > current) {
      return 'This raises it: your phone will ask for your fingerprint or '
          'face first.';
    }
    if (next < current) return 'This lowers it: it applies at once.';
    return 'This is the amount it already has.';
  }

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final effect = _effect();
    return SafeArea(
      child: SingleChildScrollView(
        padding: EdgeInsets.fromLTRB(AppSpacing.l, 0, AppSpacing.l,
            AppSpacing.l + MediaQuery.viewInsetsOf(context).bottom),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            Semantics(
                header: true,
                child: Text(widget.title, style: theme.textTheme.titleLarge)),
            const SizedBox(height: AppSpacing.m),
            TextField(
              key: const Key('eur-field'),
              controller: _text,
              autofocus: true,
              keyboardType:
                  const TextInputType.numberWithOptions(decimal: true),
              onChanged: (_) => setState(() => _problem = null),
              onSubmitted: (_) => _submit(),
              decoration: InputDecoration(
                labelText: 'Amount',
                suffixText: 'EUR',
                helperText: effect ?? widget.hint,
                helperMaxLines: 3,
                errorText: _problem,
              ),
            ),
            const SizedBox(height: AppSpacing.l),
            FilledButton(
                key: const Key('eur-save'),
                onPressed: _submit,
                child: const Text('Save')),
            const SizedBox(height: AppSpacing.s),
            OutlinedButton(
                key: const Key('eur-cancel'),
                onPressed: () => Navigator.of(context).pop(),
                child: const Text('Cancel')),
          ],
        ),
      ),
    );
  }
}

/// Asks the user to confirm a step, in a sheet from the bottom: what will happen, and (with [biometric]) that the phone will ask for a fingerprint or face. True only when confirmed.
///
/// The two buttons are full width and stacked, so neither label is cut at large text, and the safe one comes second to the primary action.
Future<bool> confirmSheet(
  BuildContext context, {
  required String title,
  required String message,
  required String confirmLabel,
  String cancelLabel = 'Keep',
  Key confirmKey = const Key('confirm-change'),
  Key cancelKey = const Key('keep'),
  IconData icon = Icons.help_outline,
  bool biometric = false,
  String? warning,
}) async {
  final yes = await showModalBottomSheet<bool>(
    context: context,
    isScrollControlled: true,
    showDragHandle: true,
    builder: (context) {
      final theme = Theme.of(context);
      return SafeArea(
        child: SingleChildScrollView(
          padding: const EdgeInsets.fromLTRB(
              AppSpacing.l, 0, AppSpacing.l, AppSpacing.l),
          child: Column(
            mainAxisSize: MainAxisSize.min,
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              Row(crossAxisAlignment: CrossAxisAlignment.start, children: [
                ExcludeSemantics(child: Icon(icon, size: 28)),
                const SizedBox(width: AppSpacing.m),
                Expanded(
                  child: Semantics(
                      header: true,
                      child: Text(title, style: theme.textTheme.titleLarge)),
                ),
              ]),
              const SizedBox(height: AppSpacing.m),
              Text(message),
              if (warning != null) ...[
                const SizedBox(height: AppSpacing.m),
                StatusBanner(
                  key: const Key('confirm-warning'),
                  severity: StatusSeverity.warning,
                  title: 'Read this first',
                  message: warning,
                ),
              ],
              if (biometric) ...[
                const SizedBox(height: AppSpacing.m),
                const StatusBanner(
                  severity: StatusSeverity.info,
                  icon: Icons.fingerprint,
                  title: 'Your phone will ask for you',
                  message: 'After you confirm, it asks for your fingerprint '
                      'or face before this applies.',
                ),
              ],
              const SizedBox(height: AppSpacing.l),
              FilledButton(
                  key: confirmKey,
                  onPressed: () => Navigator.of(context).pop(true),
                  child: Text(confirmLabel)),
              const SizedBox(height: AppSpacing.s),
              OutlinedButton(
                  key: cancelKey,
                  onPressed: () => Navigator.of(context).pop(false),
                  child: Text(cancelLabel)),
            ],
          ),
        ),
      );
    },
  );
  return yes == true;
}

/// `2026-10-20 14:30` in local time.
String whenText(DateTime t) {
  final l = t.toLocal();
  String two(int n) => n.toString().padLeft(2, '0');
  return '${l.year}-${two(l.month)}-${two(l.day)} ${two(l.hour)}:${two(l.minute)}';
}
