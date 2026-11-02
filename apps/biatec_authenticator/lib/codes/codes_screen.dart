import 'package:flutter/foundation.dart' show ValueListenable;
import 'package:flutter/material.dart';
import 'package:flutter/semantics.dart';

import '../security/settings_frame.dart' show confirmSheet;
import '../theme.dart';
import '../totp/core/otp_account.dart';
import '../totp/core/otp_generator.dart';
import '../ui/ui.dart';
import 'add/add_account_screen.dart';
import 'add/qr_scan_screen.dart';
import 'code_ticker.dart';
import 'codes_controller.dart';
import 'codes_services.dart';
import 'widgets/code_tile.dart';

/// The Codes tab: the two-factor accounts with their current codes.
///
/// The secrets are held in memory only while this screen exists, and it exists only while the app is unlocked (see `AppLockGate`); the countdowns run on one ticker that stops when the tab is hidden, the app is in the background or the screen is gone.
class CodesScreen extends StatefulWidget {
  const CodesScreen({
    super.key,
    required this.services,
    this.active,
    this.foreground,
    this.scanner = QrScanScreen.cameraScanner,
  });

  final CodesServices services;

  /// Whether the Codes tab is the one showing; the ticker runs only then. Always on when null.
  final ValueListenable<bool>? active;

  /// False while the app is in the background.
  final ValueListenable<bool>? foreground;
  final QrScannerBuilder scanner;

  @override
  State<CodesScreen> createState() => _CodesScreenState();
}

class _CodesScreenState extends State<CodesScreen> {
  late final CodesController _controller = CodesController(widget.services);
  late final CodeTicker _ticker = CodeTicker(clock: widget.services.clock);
  final Map<String, DateTime> _revealed = {};
  final _search = TextEditingController();
  bool _searching = false;
  String? _lastAnnounced;

  static const revealFor = Duration(seconds: 10);

  @override
  void initState() {
    super.initState();
    _controller.addListener(_sync);
    widget.active?.addListener(_sync);
    widget.foreground?.addListener(_sync);
    _ticker.addListener(_onTick);
    _controller.load();
  }

  @override
  void dispose() {
    widget.active?.removeListener(_sync);
    widget.foreground?.removeListener(_sync);
    _controller.removeListener(_sync);
    _ticker.removeListener(_onTick);
    _ticker.dispose();
    _controller.dispose();
    _search.dispose();
    super.dispose();
  }

  bool get _shouldTick =>
      (widget.active?.value ?? true) &&
      (widget.foreground?.value ?? true) &&
      _controller.phase == CodesPhase.ready &&
      _controller.entries.isNotEmpty;

  void _sync() {
    if (_shouldTick) {
      _ticker.start();
    } else {
      _ticker.stop();
    }
  }

  void _onTick() {
    _controller.noteShownAt(_ticker.value);
    _announce(_ticker.value);
  }

  /// Says "codes renew in 10 seconds", "in 5 seconds" and "renewed" to a screen reader, once each, and not every second.
  void _announce(DateTime now) {
    if (!mounted || !MediaQuery.accessibleNavigationOf(context)) return;
    String? text;
    for (final e in _controller.entries) {
      if (e.account.type != OtpType.totp) continue;
      final left = OtpGenerator.secondsLeft(e.account, now);
      if (left == 10) text = 'Codes renew in 10 seconds';
      if (left == 5) text = 'Codes renew in 5 seconds';
      if (left == e.account.period) text ??= 'Codes renewed';
    }
    if (text == null) return;
    final id = '$text ${now.millisecondsSinceEpoch ~/ 1000}';
    if (id == _lastAnnounced) return;
    _lastAnnounced = id;
    SemanticsService.sendAnnouncement(
        View.of(context), text, Directionality.of(context));
  }

  bool _hidden(CodeEntry e, DateTime now) {
    if (!_controller.tapToReveal) return false;
    final until = _revealed[e.info.id];
    return until == null || !now.isBefore(until);
  }

  Future<void> _tap(CodeEntry e) async {
    final now = _ticker.value;
    if (_hidden(e, now)) {
      setState(() => _revealed[e.info.id] = now.add(revealFor));
      return;
    }
    final code = OtpGenerator.code(e.account, now);
    await widget.services.clipboard.copy(code);
    if (!mounted) return;
    ScaffoldMessenger.of(context)
      ..hideCurrentSnackBar()
      ..showSnackBar(const SnackBar(
          content: Text('Code copied. It is cleared from the clipboard in 30 '
              'seconds.')));
  }

  Future<void> _add([AddMethod? start]) async {
    await Navigator.of(context).push<bool>(MaterialPageRoute(
        builder: (_) => AddAccountScreen(
            controller: _controller, scanner: widget.scanner, start: start)));
    _sync();
  }

  Future<void> _action(CodeEntry e, TileAction action) async {
    final all = _controller.entries;
    final index = all.indexOf(e);
    try {
      await _run(e, action, index);
    } on Object {
      if (!mounted) return;
      ScaffoldMessenger.of(context).showSnackBar(const SnackBar(
          content: Text('That could not be saved. Nothing was changed.')));
    }
  }

  Future<void> _run(CodeEntry e, TileAction action, int index) async {
    switch (action) {
      case TileAction.rename:
        await _rename(e);
      case TileAction.delete:
        await _delete(e);
      case TileAction.moveUp:
        await _controller.reorder(index, index - 1);
      case TileAction.moveDown:
        await _controller.reorder(index, index + 1);
    }
  }

  Future<void> _delete(CodeEntry e) async {
    final name = e.info.issuer.isEmpty
        ? e.info.label
        : '${e.info.issuer}, ${e.info.label}';
    final yes = await confirmSheet(
      context,
      title: 'Delete this account?',
      message: '$name will be removed from this phone. You will not get its '
          'codes here any more, and the key cannot be shown again. If you '
          'still need to sign in with it, set it up again at the service '
          'first.',
      warning: 'This cannot be undone.',
      confirmLabel: 'Delete',
      cancelLabel: 'Keep',
      confirmKey: const Key('confirm-delete'),
      cancelKey: const Key('keep-account'),
      biometric: true,
      icon: Icons.delete_outline,
    );
    if (!yes || !mounted) return;
    final done = await _controller.delete(e.info.id);
    if (!mounted || done) return;
    ScaffoldMessenger.of(context).showSnackBar(const SnackBar(
        content: Text('The fingerprint or face check did not pass. Nothing '
            'was deleted.')));
  }

  Future<void> _rename(CodeEntry e) async {
    await showModalBottomSheet<void>(
      context: context,
      isScrollControlled: true,
      showDragHandle: true,
      builder: (_) => _RenameSheet(entry: e, controller: _controller),
    );
  }

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      appBar: AppBar(
        title: _searching
            ? TextField(
                key: const Key('search-field'),
                controller: _search,
                autofocus: true,
                decoration: const InputDecoration(
                    hintText: 'Search accounts', border: InputBorder.none),
                onChanged: (v) => _controller.query = v,
              )
            : const Text('Codes'),
        actions: [
          IconButton(
            key: const Key('search'),
            tooltip: _searching ? 'Close search' : 'Search',
            constraints: const BoxConstraints(
                minWidth: kMinTapTarget, minHeight: kMinTapTarget),
            icon: Icon(_searching ? Icons.close : Icons.search),
            onPressed: () => setState(() {
              _searching = !_searching;
              if (!_searching) {
                _search.clear();
                _controller.query = '';
              }
            }),
          ),
          ListenableBuilder(
            listenable: _controller,
            builder: (context, _) => IconButton(
              key: const Key('tap-to-reveal'),
              tooltip: _controller.tapToReveal
                  ? 'Show codes without tapping'
                  : 'Hide codes until tapped',
              constraints: const BoxConstraints(
                  minWidth: kMinTapTarget, minHeight: kMinTapTarget),
              icon: Icon(_controller.tapToReveal
                  ? Icons.visibility_off
                  : Icons.visibility),
              onPressed: () =>
                  _controller.setTapToReveal(!_controller.tapToReveal),
            ),
          ),
        ],
      ),
      floatingActionButton: ListenableBuilder(
        listenable: _controller,
        builder: (context, _) => _controller.phase == CodesPhase.ready &&
                _controller.entries.isNotEmpty
            ? FloatingActionButton.extended(
                key: const Key('add-account'),
                onPressed: _add,
                icon: const Icon(Icons.add),
                label: const Text('Add account'),
              )
            : const SizedBox.shrink(),
      ),
      body: ListenableBuilder(
        listenable: Listenable.merge(
            [_controller, _ticker, widget.services.serverClock]),
        builder: (context, _) => _body(context),
      ),
    );
  }

  Widget _body(BuildContext context) {
    switch (_controller.phase) {
      case CodesPhase.loading:
        return const LoadingSkeleton(key: Key('codes-loading'));
      case CodesPhase.failed:
        return ErrorState(
          key: const Key('codes-failed'),
          title: 'Your codes could not be opened',
          message: _controller.problem ?? 'Try again.',
          onRetry: _controller.load,
        );
      case CodesPhase.ready:
        break;
    }
    if (_controller.entries.isEmpty) return _empty();
    final now = _ticker.value;
    final plausible =
        OtpGenerator.clockIsPlausible(now, notBefore: _controller.latestSeen);
    final all = _controller.entries;
    final shown = _controller.visible
        .where((e) => plausible || e.account.type == OtpType.hotp)
        .toList();
    final searching = _controller.query.isNotEmpty;
    return Column(children: [
      if (!plausible)
        const Padding(
          padding: EdgeInsets.all(AppSpacing.s),
          child: StatusBanner(
            key: Key('clock-reset'),
            severity: StatusSeverity.danger,
            title: 'Your phone\'s clock looks wrong',
            message: 'The date is earlier than one this app has already seen, '
                'so time-based codes would be wrong and are hidden. Turn on '
                'automatic date and time in your phone\'s settings.',
          ),
        )
      else if (widget.services.serverClock.isOff)
        Padding(
          padding: const EdgeInsets.all(AppSpacing.s),
          child: StatusBanner(
            key: const Key('clock-off'),
            severity: StatusSeverity.warning,
            title: 'Your phone\'s clock is off',
            message: 'It is ${_offBy()} from the server. Codes made from a '
                'wrong clock are refused by the service. Turn on automatic '
                'date and time in your phone\'s settings.',
          ),
        ),
      Expanded(
        child: shown.isEmpty
            ? Center(
                child: Padding(
                  padding: const EdgeInsets.all(AppSpacing.l),
                  child: Text(
                      searching
                          ? 'No account matches "${_controller.query}".'
                          : 'No codes can be shown.',
                      key: const Key('no-match'),
                      textAlign: TextAlign.center),
                ),
              )
            : _list(shown, all, now, canReorder: !searching),
      ),
    ]);
  }

  String _offBy() {
    final d = widget.services.serverClock.offset!;
    final secs = d.abs().inSeconds;
    final how = secs >= 120 ? '${d.abs().inMinutes} minutes' : '$secs seconds';
    return d.isNegative ? '$how ahead' : '$how behind';
  }

  Widget _list(List<CodeEntry> shown, List<CodeEntry> all, DateTime now,
      {required bool canReorder}) {
    Widget tile(CodeEntry e, int position) {
      final index = all.indexOf(e);
      return CodeTile(
        key: ValueKey(e.info.id),
        entry: e,
        now: now,
        hidden: _hidden(e, now),
        onTap: () => _tap(e),
        onAction: (a) => _action(e, a),
        onNextCounter: () => _controller.nextCounter(e.info.id),
        canMoveUp: canReorder && index > 0,
        canMoveDown: canReorder && index < all.length - 1,
        dragHandle: canReorder && all.length > 1
            ? ReorderableDragStartListener(
                index: position,
                child: const SizedBox(
                  width: kMinTapTarget,
                  height: kMinTapTarget,
                  child: ExcludeSemantics(child: Icon(Icons.drag_handle)),
                ),
              )
            : null,
      );
    }

    const padding =
        EdgeInsets.fromLTRB(AppSpacing.s, AppSpacing.s, AppSpacing.s, 96);
    if (!canReorder) {
      return ListView.builder(
        padding: padding,
        itemCount: shown.length,
        itemBuilder: (_, i) => tile(shown[i], i),
      );
    }
    return ReorderableListView.builder(
      key: const Key('code-list'),
      padding: padding,
      buildDefaultDragHandles: false,
      itemCount: shown.length,
      onReorderItem: (from, to) => _controller.reorder(from, to),
      itemBuilder: (_, i) => tile(shown[i], i),
    );
  }

  Widget _empty() {
    return ScrollableCenter(
      key: const Key('codes-empty'),
      child: Column(
        mainAxisSize: MainAxisSize.min,
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          const ExcludeSemantics(child: Icon(Icons.pin_outlined, size: 56)),
          const SizedBox(height: AppSpacing.m),
          Semantics(
            header: true,
            child: Text('No codes yet',
                textAlign: TextAlign.center,
                style: Theme.of(context).textTheme.titleLarge),
          ),
          const SizedBox(height: AppSpacing.s),
          const Text(
              'Add an account from any service that asks for an authenticator '
              'app. Pick the way it gives you the account.',
              textAlign: TextAlign.center),
          const SizedBox(height: AppSpacing.l),
          FilledButton.icon(
            key: const Key('empty-scan'),
            onPressed: () => _add(AddMethod.scan),
            icon: const Icon(Icons.qr_code_scanner),
            label: const Text('Scan a QR code'),
          ),
          const SizedBox(height: AppSpacing.s),
          OutlinedButton.icon(
            key: const Key('empty-link'),
            onPressed: () => _add(AddMethod.link),
            icon: const Icon(Icons.link),
            label: const Text('Paste a link'),
          ),
          const SizedBox(height: AppSpacing.s),
          OutlinedButton.icon(
            key: const Key('empty-key'),
            onPressed: () => _add(AddMethod.key),
            icon: const Icon(Icons.keyboard),
            label: const Text('Enter a setup key'),
          ),
        ],
      ),
    );
  }
}

class _RenameSheet extends StatefulWidget {
  const _RenameSheet({required this.entry, required this.controller});

  final CodeEntry entry;
  final CodesController controller;

  @override
  State<_RenameSheet> createState() => _RenameSheetState();
}

class _RenameSheetState extends State<_RenameSheet> {
  late final _issuer = TextEditingController(text: widget.entry.info.issuer);
  late final _label = TextEditingController(text: widget.entry.info.label);
  String? _problem;

  @override
  void dispose() {
    _issuer.dispose();
    _label.dispose();
    super.dispose();
  }

  Future<void> _save() async {
    try {
      await widget.controller.rename(widget.entry.info.id,
          issuer: _issuer.text, label: _label.text);
      if (mounted) Navigator.of(context).pop();
    } on OtpAccountException catch (e) {
      setState(() => _problem = e.message as String);
    } on Object {
      setState(() => _problem = 'The name could not be saved. Try again.');
    }
  }

  @override
  Widget build(BuildContext context) {
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
                child: Text('Rename',
                    style: Theme.of(context).textTheme.titleLarge)),
            const SizedBox(height: AppSpacing.m),
            TextField(
              key: const Key('rename-issuer'),
              controller: _issuer,
              decoration: const InputDecoration(labelText: 'Service'),
            ),
            const SizedBox(height: AppSpacing.m),
            TextField(
              key: const Key('rename-label'),
              controller: _label,
              decoration: InputDecoration(
                  labelText: 'Account name', errorText: _problem),
            ),
            const SizedBox(height: AppSpacing.l),
            FilledButton(
                key: const Key('rename-save'),
                onPressed: _save,
                child: const Text('Save')),
            const SizedBox(height: AppSpacing.s),
            OutlinedButton(
                key: const Key('rename-cancel'),
                onPressed: () => Navigator.of(context).pop(),
                child: const Text('Cancel')),
          ],
        ),
      ),
    );
  }
}
