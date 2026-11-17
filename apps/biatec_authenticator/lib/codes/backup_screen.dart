import 'package:material_ui/material_ui.dart';

import '../theme.dart';
import '../totp/backup/backup_format.dart';
import '../totp/backup/backup_service.dart';
import '../totp/backup/passphrase.dart';
import '../totp/core/otp_account.dart';
import '../ui/ui.dart';
import 'codes_controller.dart';
import 'codes_texts.dart';

/// Backup and restore of the two-factor accounts: an encrypted file the user keeps, locked with a passphrase only they know.
class BackupScreen extends StatelessWidget {
  const BackupScreen(
      {super.key, required this.service, required this.controller});

  final BackupService service;
  final CodesController controller;

  @override
  Widget build(BuildContext context) {
    final t = CodesTexts.from(context);
    return Scaffold(
      appBar: AppBar(title: Text(t.text('backupTitle'))),
      body: ListView(
        padding: const EdgeInsets.all(AppSpacing.m),
        children: [
          Text(t.text('backupIntro')),
          const SizedBox(height: AppSpacing.m),
          Card(
            child: ListTile(
              key: const Key('backup-create'),
              leading: const Icon(Icons.upload_file),
              title: Text(t.text('createBackup')),
              subtitle: Text(t.text('createBackupText')),
              trailing: const Icon(Icons.chevron_right),
              onTap: () => Navigator.of(context).push(MaterialPageRoute<void>(
                  builder: (_) => ExportBackupScreen(service: service))),
            ),
          ),
          Card(
            child: ListTile(
              key: const Key('backup-restore'),
              leading: const Icon(Icons.download),
              title: Text(t.text('restoreTitle')),
              subtitle: Text(t.text('restoreText')),
              trailing: const Icon(Icons.chevron_right),
              onTap: () => Navigator.of(context).push(MaterialPageRoute<void>(
                  builder: (_) => ImportBackupScreen(
                      service: service, controller: controller))),
            ),
          ),
        ],
      ),
    );
  }
}

/// The passphrase fields: hidden, with a switch to show them, a strength line in words and a bar, and the rule that is not met.
class _PassphraseFields extends StatelessWidget {
  const _PassphraseFields({
    required this.passphrase,
    required this.confirmation,
    required this.shown,
    required this.onShow,
    required this.onChanged,
    required this.verdict,
    required this.enabled,
  });

  final TextEditingController passphrase;
  final TextEditingController? confirmation;
  final bool shown;
  final VoidCallback onShow;
  final VoidCallback onChanged;
  final PassphraseVerdict? verdict;
  final bool enabled;

  static String word(CodesTexts t, PassphraseStrength s) => t.text(switch (s) {
        PassphraseStrength.refused => 'strengthRefused',
        PassphraseStrength.fair => 'strengthFair',
        PassphraseStrength.good => 'strengthGood',
        PassphraseStrength.strong => 'strengthStrong',
      });

  @override
  Widget build(BuildContext context) {
    final t = CodesTexts.from(context);
    final v = verdict;
    return Column(crossAxisAlignment: CrossAxisAlignment.stretch, children: [
      TextField(
        key: const Key('passphrase'),
        controller: passphrase,
        enabled: enabled,
        obscureText: !shown,
        autocorrect: false,
        enableSuggestions: false,
        onChanged: (_) => onChanged(),
        decoration: InputDecoration(
          labelText: t.text('passphrase'),
          helperText: t.text('passphraseHelp'),
          helperMaxLines: 2,
          suffixIcon: IconButton(
            key: const Key('show-passphrase'),
            tooltip: t.text(shown ? 'hidePassphrase' : 'showPassphrase'),
            constraints: const BoxConstraints(
                minWidth: kMinTapTarget, minHeight: kMinTapTarget),
            icon: Icon(shown ? Icons.visibility_off : Icons.visibility),
            onPressed: onShow,
          ),
        ),
      ),
      if (v != null && passphrase.text.isNotEmpty) ...[
        const SizedBox(height: AppSpacing.s),
        Semantics(
          liveRegion: true,
          container: true,
          child:
              Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
            Text(t.text('strength', {'word': word(t, v.strength)}),
                key: const Key('passphrase-strength')),
            const SizedBox(height: AppSpacing.xs),
            ExcludeSemantics(
              child: LinearProgressIndicator(
                value: (v.bits / 100).clamp(0.05, 1.0),
                color: v.accepted ? null : Theme.of(context).colorScheme.error,
              ),
            ),
            if (v.problem != null)
              Padding(
                padding: const EdgeInsets.only(top: AppSpacing.xs),
                child: Text(v.problem!,
                    key: const Key('passphrase-problem'),
                    style:
                        TextStyle(color: Theme.of(context).colorScheme.error)),
              ),
          ]),
        ),
      ],
      if (confirmation != null) ...[
        const SizedBox(height: AppSpacing.m),
        TextField(
          key: const Key('passphrase-confirm'),
          controller: confirmation,
          enabled: enabled,
          obscureText: !shown,
          autocorrect: false,
          enableSuggestions: false,
          onChanged: (_) => onChanged(),
          decoration: InputDecoration(labelText: t.text('typeAgain')),
        ),
      ],
    ]);
  }
}

/// Creates the backup: the passphrase twice, a warning that it cannot be recovered, progress while the slow key derivation runs, then the share sheet.
class ExportBackupScreen extends StatefulWidget {
  const ExportBackupScreen({super.key, required this.service});

  final BackupService service;

  @override
  State<ExportBackupScreen> createState() => _ExportBackupScreenState();
}

class _ExportBackupScreenState extends State<ExportBackupScreen> {
  final _pass = TextEditingController();
  final _again = TextEditingController();
  bool _shown = false;
  bool _busy = false;
  String? _problem;
  int? _done;

  @override
  void dispose() {
    _pass.clear();
    _again.clear();
    _pass.dispose();
    _again.dispose();
    super.dispose();
  }

  PassphraseVerdict get _verdict =>
      Passphrase.check(_pass.text, confirmation: _again.text);

  bool get _ready => _verdict.accepted && _again.text.isNotEmpty && !_busy;

  Future<void> _create() async {
    setState(() {
      _busy = true;
      _problem = null;
      _done = null;
    });
    try {
      final n = await widget.service.export(_pass.text, _again.text);
      if (!mounted) return;
      // The passphrase is not kept around once the file exists.
      _pass.clear();
      _again.clear();
      setState(() => _done = n);
    } on BackupRefused catch (e) {
      if (mounted) setState(() => _problem = e.message);
    } on Object {
      if (mounted) {
        setState(
            () => _problem = CodesTexts.from(context).text('backupFailed'));
      }
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    final t = CodesTexts.from(context);
    final v = _verdict;
    // No way out while the work runs: leaving would wipe the accounts being added.
    return PopScope(
        canPop: !_busy,
        child: Scaffold(
          appBar: AppBar(title: Text(t.text('createBackup'))),
          body: ListView(
            padding: const EdgeInsets.all(AppSpacing.m),
            children: [
              StatusBanner(
                key: const Key('backup-warning'),
                severity: StatusSeverity.warning,
                title: t.text('noRecoveryTitle'),
                message: t.text('noRecovery'),
              ),
              const SizedBox(height: AppSpacing.m),
              _PassphraseFields(
                passphrase: _pass,
                confirmation: _again,
                shown: _shown,
                onShow: () => setState(() => _shown = !_shown),
                onChanged: () => setState(() {}),
                verdict: _pass.text.isEmpty ? null : v,
                enabled: !_busy,
              ),
              if (_pass.text.isNotEmpty &&
                  _again.text.isNotEmpty &&
                  _pass.text != _again.text)
                Padding(
                  padding: const EdgeInsets.only(top: AppSpacing.s),
                  child: Text(t.text('passphraseMismatch'),
                      key: const Key('passphrase-mismatch')),
                ),
              const SizedBox(height: AppSpacing.l),
              if (_problem != null)
                Padding(
                  padding: const EdgeInsets.only(bottom: AppSpacing.m),
                  child: Semantics(
                    liveRegion: true,
                    child: StatusBanner(
                        key: const Key('backup-problem'),
                        severity: StatusSeverity.danger,
                        title: t.text('noBackupMade'),
                        message: _problem!),
                  ),
                ),
              if (_done != null)
                Padding(
                  padding: const EdgeInsets.only(bottom: AppSpacing.m),
                  child: Semantics(
                    liveRegion: true,
                    child: StatusBanner(
                        key: const Key('backup-done'),
                        severity: StatusSeverity.success,
                        title: t.text('backupReady'),
                        message: t.count('backupDone', _done!)),
                  ),
                ),
              if (_busy)
                Padding(
                  padding: const EdgeInsets.only(bottom: AppSpacing.m),
                  child: Column(children: [
                    const LinearProgressIndicator(key: Key('backup-progress')),
                    const SizedBox(height: AppSpacing.s),
                    Text(t.text('protecting')),
                  ]),
                ),
              FilledButton.icon(
                key: const Key('backup-make'),
                onPressed: _ready ? _create : null,
                icon: const Icon(Icons.lock),
                label: Text(t.text(_busy ? 'working' : 'createTheBackup')),
              ),
            ],
          ),
        ));
  }
}

/// Restores from a backup file: the passphrase, a fresh biometric, the file, a preview of what will be added, then the accounts. After a change of the phone's biometrics ([afterBiometricsChanged]) the old vault is replaced by a new one.
class ImportBackupScreen extends StatefulWidget {
  const ImportBackupScreen(
      {super.key,
      required this.service,
      this.controller,
      this.afterBiometricsChanged = false});

  final BackupService service;

  /// The list the accounts are added to (not used after a biometrics change, where there is no list yet).
  final CodesController? controller;
  final bool afterBiometricsChanged;

  @override
  State<ImportBackupScreen> createState() => _ImportBackupScreenState();
}

class _ImportBackupScreenState extends State<ImportBackupScreen> {
  final _pass = TextEditingController();
  bool _shown = false;
  bool _busy = false;
  String? _problem;
  ImportPreview? _preview;
  int? _added;

  @override
  void dispose() {
    _preview?.wipe();
    _pass.clear();
    _pass.dispose();
    super.dispose();
  }

  Future<void> _choose() async {
    setState(() {
      _busy = true;
      _problem = null;
      _added = null;
    });
    try {
      _preview?.wipe();
      _preview = null;
      final existing = widget.controller == null
          ? const <OtpAccount>[]
          : [for (final e in widget.controller!.entries) e.account];
      final preview = widget.afterBiometricsChanged
          ? await widget.service.openAfterBiometricsChanged(_pass.text)
          : await widget.service.open(_pass.text, existing: existing);
      if (!mounted) {
        preview?.wipe();
        return;
      }
      // The passphrase did its one job: it is not kept in the field.
      if (preview != null) _pass.clear();
      setState(() => _preview = preview);
    } on BackupRefused catch (e) {
      if (mounted) setState(() => _problem = e.message);
    } on BackupFailure {
      if (mounted) setState(() => _problem = BackupFailure.message);
    } on Object {
      if (mounted) {
        setState(() => _problem = CodesTexts.from(context).text('readFailed'));
      }
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  Future<void> _apply() async {
    final preview = _preview;
    if (preview == null) return;
    setState(() {
      _busy = true;
      _problem = null;
    });
    try {
      var n = 0;
      if (widget.afterBiometricsChanged) {
        n = await widget.service.restoreIntoFreshVault(preview);
      } else {
        // An account leaves the preview once it is in the list, so a retry after a failure adds only the rest.
        for (final a in List<OtpAccount>.of(preview.toAdd)) {
          final already =
              widget.controller!.entries.any((e) => a.isDuplicateOf(e.account));
          // Only a duplicate is skipped; any other refusal (a list that could not be opened, a lock) stops here and keeps the rest for a retry.
          if (!already) {
            await widget.controller!.add(a);
            n++;
          }
          preview.toAdd.remove(a);
          a.wipe();
        }
      }
      if (!mounted) return;
      _pass.clear();
      if (widget.afterBiometricsChanged) {
        // The app is back on its lock screen: this one has nothing left to show.
        Navigator.of(context).pop();
        return;
      }
      setState(() {
        _preview = null;
        _added = n;
      });
    } on AddRefused catch (e) {
      if (mounted) setState(() => _problem = e.message);
    } on Object {
      if (mounted) {
        setState(
            () => _problem = CodesTexts.from(context).text('addAllFailed'));
      }
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    final t = CodesTexts.from(context);
    final preview = _preview;
    // No way out while the work runs: leaving would wipe the accounts being added.
    return PopScope(
        canPop: !_busy,
        child: Scaffold(
          appBar: AppBar(title: Text(t.text('restoreTitle'))),
          body: ListView(
            padding: const EdgeInsets.all(AppSpacing.m),
            children: [
              if (widget.afterBiometricsChanged)
                Padding(
                  padding: const EdgeInsets.only(bottom: AppSpacing.m),
                  child: Text(t.text('afterBiometrics')),
                ),
              _PassphraseFields(
                passphrase: _pass,
                confirmation: null,
                shown: _shown,
                onShow: () => setState(() => _shown = !_shown),
                onChanged: () => setState(() {}),
                verdict: null,
                enabled: !_busy && preview == null,
              ),
              const SizedBox(height: AppSpacing.l),
              if (_problem != null)
                Padding(
                  padding: const EdgeInsets.only(bottom: AppSpacing.m),
                  child: Semantics(
                    liveRegion: true,
                    child: StatusBanner(
                        key: const Key('restore-problem'),
                        severity: StatusSeverity.danger,
                        title: t.text('nothingAdded'),
                        message: _problem!),
                  ),
                ),
              if (_added != null)
                Padding(
                  padding: const EdgeInsets.only(bottom: AppSpacing.m),
                  child: Semantics(
                    liveRegion: true,
                    child: StatusBanner(
                        key: const Key('restore-done'),
                        severity: StatusSeverity.success,
                        title: t.text('restored'),
                        message: t.count('restored', _added!)),
                  ),
                ),
              if (_busy)
                const LinearProgressIndicator(key: Key('restore-progress')),
              if (preview == null)
                FilledButton.icon(
                  key: const Key('restore-choose'),
                  onPressed: _busy || _pass.text.isEmpty ? null : _choose,
                  icon: const Icon(Icons.folder_open),
                  label: Text(t.text('chooseFile')),
                )
              else ...[
                Semantics(
                  header: true,
                  child: Text(
                      preview.toAdd.isEmpty
                          ? t.text('nothingNew')
                          : t.count('willAdd', preview.toAdd.length),
                      key: const Key('restore-heading'),
                      style: Theme.of(context).textTheme.titleMedium),
                ),
                for (final a in preview.toAdd)
                  ListTile(
                    key: Key('restore-item-${preview.toAdd.indexOf(a)}'),
                    title: Text(a.issuer.isEmpty ? a.label : a.issuer),
                    subtitle: a.issuer.isEmpty ? null : Text(a.label),
                  ),
                if (preview.duplicates > 0)
                  Padding(
                    padding: const EdgeInsets.symmetric(vertical: AppSpacing.s),
                    child: Text(t.count('duplicates', preview.duplicates),
                        key: const Key('restore-duplicates')),
                  ),
                const SizedBox(height: AppSpacing.m),
                FilledButton(
                  key: const Key('restore-apply'),
                  onPressed: _busy || preview.toAdd.isEmpty ? null : _apply,
                  child: Text(t.text(widget.afterBiometricsChanged
                      ? 'restoreThese'
                      : 'addThese')),
                ),
              ],
            ],
          ),
        ));
  }
}
