import 'package:flutter/material.dart';

import '../shell/tab_reload.dart';
import '../ui/ui.dart';
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
  });

  final String title;
  final SettingsServices services;

  /// The screen body once loaded.
  final List<Widget> Function(BuildContext, SettingsController) builder;

  /// Shown below the body whatever state it is in.
  final Widget? footer;

  @override
  State<SettingsFrame> createState() => _SettingsFrameState();
}

class _SettingsFrameState extends State<SettingsFrame>
    with TabReloadListener {
  late final SettingsController _controller;

  @override
  void initState() {
    super.initState();
    _controller = widget.services.controller();
    if (widget.services.enabled) _controller.load();
  }

  @override
  void onReload() {
    if (widget.services.enabled) _controller.load(quiet: true);
  }

  @override
  void dispose() {
    _controller.dispose();
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
          ? const ScrollableCenter(
              child: EmptyState(
                key: Key('not-available'),
                icon: Icons.info_outline,
                title: 'Not available yet',
                message: 'This Biatec OIDC does not offer device and settings '
                    'management; it only runs against the mock for now.',
              ),
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
              return ListView(
                children: [
                  if (c.busy) const LinearProgressIndicator(),
                  if (c.error != null)
                    _Status(
                        key: const Key('status-error'),
                        icon: Icons.error_outline,
                        label: 'Problem',
                        text: c.error!),
                  if (c.notice != null)
                    _Status(
                        key: const Key('status-notice'),
                        icon: Icons.check_circle_outline,
                        label: 'Done',
                        text: c.notice!),
                  ...widget.builder(context, c),
                ],
              );
          }
        },
      ),
    );
  }
}

class _Status extends StatelessWidget {
  const _Status(
      {super.key, required this.icon, required this.label, required this.text});

  final IconData icon;
  final String label;
  final String text;

  @override
  Widget build(BuildContext context) {
    return Semantics(
      liveRegion: true,
      child: ListTile(
        leading: Icon(icon),
        title: Text(label, style: Theme.of(context).textTheme.labelLarge),
        subtitle: Text(text),
      ),
    );
  }
}

/// A decimal EUR amount as Biatec OIDC takes it (`250`, `250.50`).
final RegExp eurFormat = RegExp(r'^(0|[1-9][0-9]*)(\.[0-9]{1,2})?$');

/// Asks for a EUR amount. Null when cancelled.
Future<String?> askEur(BuildContext context,
    {required String title, required String initial, required String hint}) {
  return showDialog<String>(
    context: context,
    builder: (_) => _EurDialog(title: title, initial: initial, hint: hint),
  );
}

class _EurDialog extends StatefulWidget {
  const _EurDialog(
      {required this.title, required this.initial, required this.hint});

  final String title;
  final String initial;
  final String hint;

  @override
  State<_EurDialog> createState() => _EurDialogState();
}

class _EurDialogState extends State<_EurDialog> {
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

  @override
  Widget build(BuildContext context) {
    return AlertDialog(
      title: Text(widget.title),
      content: Column(
        mainAxisSize: MainAxisSize.min,
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          TextField(
            key: const Key('eur-field'),
            controller: _text,
            autofocus: true,
            keyboardType: const TextInputType.numberWithOptions(decimal: true),
            decoration: InputDecoration(
                labelText: 'Amount in EUR',
                helperText: widget.hint,
                errorText: _problem),
            onSubmitted: (_) => _submit(),
          ),
        ],
      ),
      actions: [
        TextButton(
            onPressed: () => Navigator.of(context).pop(),
            child: const Text('Cancel')),
        FilledButton(
            key: const Key('eur-save'),
            onPressed: _submit,
            child: const Text('Save')),
      ],
    );
  }
}

/// `2026-10-20 14:30` in local time.
String whenText(DateTime t) {
  final l = t.toLocal();
  String two(int n) => n.toString().padLeft(2, '0');
  return '${l.year}-${two(l.month)}-${two(l.day)} ${two(l.hour)}:${two(l.minute)}';
}
