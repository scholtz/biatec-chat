import 'package:material_ui/material_ui.dart';

import '../../theme.dart';
import '../../totp/core/otp_account.dart';
import '../../ui/ui.dart';
import '../../totp/vault/vault_key_store.dart';
import '../codes_controller.dart';
import '../codes_texts.dart';
import 'account_input.dart';
import 'qr_scan_screen.dart';

enum _Step { choose, link, key, preview }

/// Which way to start in, when the empty Codes tab offers the three ways directly.
enum AddMethod { scan, link, key }

/// Adds an account: by scanning a QR code, by pasting an `otpauth://` link, or by typing the setup key. Whatever the way, the account is shown (who it is and how its codes are made) before anything is saved, a copy of an account already in the list is refused, and a refused entry says why in words.
class AddAccountScreen extends StatefulWidget {
  const AddAccountScreen(
      {super.key,
      required this.controller,
      this.scanner = QrScanScreen.cameraScanner,
      this.start});

  final CodesController controller;
  final QrScannerBuilder scanner;
  final AddMethod? start;

  @override
  State<AddAccountScreen> createState() => _AddAccountScreenState();
}

class _AddAccountScreenState extends State<AddAccountScreen> {
  _Step _step = _Step.choose;
  OtpAccount? _account;
  String? _problem;
  bool _saving = false;

  final _link = TextEditingController();
  final _issuer = TextEditingController();
  final _label = TextEditingController();
  final _secret = TextEditingController();
  OtpAlgorithm _algorithm = OtpAlgorithm.sha1;
  int _digits = 6;
  int _period = OtpLimits.defaultPeriod;

  CodesTexts get _t => CodesTexts.from(context);

  @override
  void initState() {
    super.initState();
    switch (widget.start) {
      case AddMethod.link:
        _step = _Step.link;
      case AddMethod.key:
        _step = _Step.key;
      case AddMethod.scan:
        WidgetsBinding.instance.addPostFrameCallback((_) {
          if (mounted) _scan();
        });
      case null:
        break;
    }
  }

  @override
  void dispose() {
    _account?.wipe();
    _link.dispose();
    _issuer.dispose();
    _label.dispose();
    _secret.dispose();
    super.dispose();
  }

  void _preview(OtpAccount account) {
    _account?.wipe();
    setState(() {
      _account = account;
      _problem = null;
      _step = _Step.preview;
    });
  }

  void _try(OtpAccount Function() make) {
    try {
      _preview(make());
    } on InputProblem catch (e) {
      setState(() => _problem = e.message);
    }
  }

  Future<void> _scan() async {
    final text = await Navigator.of(context).push<String>(MaterialPageRoute(
        builder: (_) => QrScanScreen(builder: widget.scanner)));
    if (text == null || !mounted) return;
    try {
      _preview(AccountInput.fromLink(text));
    } on InputProblem catch (e) {
      setState(() {
        _problem = _t.text('qrNotAccount', {'why': e.message});
        _step = _Step.choose;
      });
    }
  }

  Future<void> _save() async {
    final account = _account;
    if (account == null || _saving) return;
    setState(() {
      _saving = true;
      _problem = null;
    });
    try {
      await widget.controller.add(account);
      if (mounted) Navigator.of(context).pop(true);
    } on AddRefused catch (e) {
      _stop(e.message);
    } on VaultAuthFailed {
      _stop(_t.text('saveAuthFailed'));
    } on VaultBiometricsChanged {
      _stop(_t.text('saveBiometricsChanged'));
    } on Object {
      _stop(_t.text('saveFailed'));
    }
  }

  void _stop(String message) {
    if (!mounted) return;
    setState(() {
      _saving = false;
      _problem = message;
    });
  }

  @override
  Widget build(BuildContext context) {
    final t = _t;
    final title = t.text(switch (_step) {
      _Step.choose => 'addTitle',
      _Step.link => 'pasteLink',
      _Step.key => 'enterKey',
      _Step.preview => 'checkTitle',
    });
    return PopScope(
      canPop: _step == _Step.choose,
      onPopInvokedWithResult: (didPop, _) {
        if (!didPop) {
          setState(() {
            _problem = null;
            _step = _Step.choose;
          });
        }
      },
      child: Scaffold(
        appBar: AppBar(title: Text(title)),
        body: ListView(
          padding: const EdgeInsets.all(AppSpacing.m),
          children: [
            if (_problem != null)
              Padding(
                padding: const EdgeInsets.only(bottom: AppSpacing.m),
                child: Semantics(
                  liveRegion: true,
                  child: StatusBanner(
                    key: const Key('add-problem'),
                    severity: StatusSeverity.danger,
                    title: t.text('notAdded'),
                    message: _problem!,
                  ),
                ),
              ),
            ...switch (_step) {
              _Step.choose => _choose(),
              _Step.link => _linkForm(),
              _Step.key => _keyForm(),
              _Step.preview => _previewCard(),
            },
          ],
        ),
      ),
    );
  }

  List<Widget> _choose() => [
        Padding(
          padding: const EdgeInsets.only(bottom: AppSpacing.s),
          child: Text(_t.text('pickWay')),
        ),
        _Way(
            keyName: 'add-scan',
            icon: Icons.qr_code_scanner,
            title: _t.text('scanQr'),
            text: _t.text('scanText'),
            onTap: _scan),
        _Way(
            keyName: 'add-link',
            icon: Icons.link,
            title: _t.text('pasteLink'),
            text: _t.text('linkText'),
            onTap: () => setState(() {
                  _problem = null;
                  _step = _Step.link;
                })),
        _Way(
            keyName: 'add-key',
            icon: Icons.keyboard,
            title: _t.text('enterKey'),
            text: _t.text('keyText'),
            onTap: () => setState(() {
                  _problem = null;
                  _step = _Step.key;
                })),
      ];

  List<Widget> _linkForm() => [
        TextField(
          key: const Key('link-field'),
          controller: _link,
          autocorrect: false,
          enableSuggestions: false,
          keyboardType: TextInputType.url,
          minLines: 2,
          maxLines: 4,
          decoration: InputDecoration(
              labelText: _t.text('linkLabel'), helperText: _t.text('linkHelp')),
        ),
        const SizedBox(height: AppSpacing.l),
        FilledButton(
          key: const Key('link-continue'),
          onPressed: () => _try(() => AccountInput.fromLink(_link.text)),
          child: Text(_t.text('continue')),
        ),
      ];

  List<Widget> _keyForm() => [
        TextField(
          key: const Key('key-issuer'),
          controller: _issuer,
          textInputAction: TextInputAction.next,
          decoration: InputDecoration(
              labelText: _t.text('service'),
              helperText: _t.text('serviceHelp')),
        ),
        const SizedBox(height: AppSpacing.m),
        TextField(
          key: const Key('key-label'),
          controller: _label,
          textInputAction: TextInputAction.next,
          decoration: InputDecoration(
              labelText: _t.text('accountName'),
              helperText: _t.text('accountNameHelp')),
        ),
        const SizedBox(height: AppSpacing.m),
        TextField(
          key: const Key('key-secret'),
          controller: _secret,
          autocorrect: false,
          enableSuggestions: false,
          decoration: InputDecoration(
              labelText: _t.text('setupKey'),
              helperText: _t.text('setupKeyHelp')),
        ),
        const SizedBox(height: AppSpacing.m),
        ExpansionTile(
          key: const Key('key-advanced'),
          title: Text(_t.text('moreSettings')),
          subtitle: Text(_t.text('moreSettingsHelp')),
          childrenPadding: const EdgeInsets.only(bottom: AppSpacing.s),
          children: [
            DropdownButtonFormField<OtpAlgorithm>(
              key: const Key('key-algorithm'),
              initialValue: _algorithm,
              decoration: InputDecoration(labelText: _t.text('algorithm')),
              items: [
                for (final a in OtpAlgorithm.values)
                  DropdownMenuItem(value: a, child: Text(a.uriName)),
              ],
              onChanged: (v) => setState(() => _algorithm = v ?? _algorithm),
            ),
            const SizedBox(height: AppSpacing.m),
            DropdownButtonFormField<int>(
              key: const Key('key-digits'),
              initialValue: _digits,
              decoration: InputDecoration(labelText: _t.text('digits')),
              items: [
                for (var d = OtpLimits.minDigits; d <= OtpLimits.maxDigits; d++)
                  DropdownMenuItem(value: d, child: Text('$d')),
              ],
              onChanged: (v) => setState(() => _digits = v ?? _digits),
            ),
            const SizedBox(height: AppSpacing.m),
            DropdownButtonFormField<int>(
              key: const Key('key-period'),
              initialValue: _period,
              decoration: InputDecoration(labelText: _t.text('period')),
              items: [
                for (final p in const [15, 30, 60, 90, 120])
                  DropdownMenuItem(value: p, child: Text('$p')),
              ],
              onChanged: (v) => setState(() => _period = v ?? _period),
            ),
          ],
        ),
        const SizedBox(height: AppSpacing.l),
        FilledButton(
          key: const Key('key-continue'),
          onPressed: () => _try(() => AccountInput.fromKey(
                issuer: _issuer.text,
                label: _label.text,
                key: _secret.text,
                algorithm: _algorithm,
                digits: _digits,
                period: _period,
              )),
          child: Text(_t.text('continue')),
        ),
      ];

  List<Widget> _previewCard() {
    final account = _account!;
    final theme = Theme.of(context);
    final hotp = account.type == OtpType.hotp;
    final t = _t;
    return [
      Card(
        key: const Key('add-preview'),
        child: Padding(
          padding: const EdgeInsets.all(AppSpacing.m),
          child:
              Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
            Text(
                account.issuer.isEmpty
                    ? t.text('noServiceName')
                    : account.issuer,
                style: theme.textTheme.titleLarge),
            Text(account.label, style: theme.textTheme.titleMedium),
            const SizedBox(height: AppSpacing.m),
            Text(hotp
                ? t.text('counterBased', {'counter': account.counter})
                : t.text('timeBased', {'period': account.period})),
            Text(t.text('digitsLine', {
              'digits': account.digits,
              'algorithm': account.algorithm.uriName
            })),
            Text(t.text('keyStrength', {'bits': account.secretBits})),
          ]),
        ),
      ),
      const SizedBox(height: AppSpacing.s),
      Text(t.text('checkText')),
      const SizedBox(height: AppSpacing.l),
      FilledButton(
        key: const Key('save-account'),
        onPressed: _saving ? null : _save,
        child: Text(t.text(_saving ? 'saving' : 'addAccount')),
      ),
    ];
  }
}

class _Way extends StatelessWidget {
  const _Way(
      {required this.keyName,
      required this.icon,
      required this.title,
      required this.text,
      required this.onTap});

  final String keyName;
  final IconData icon;
  final String title;
  final String text;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    return Card(
      child: ListTile(
        key: Key(keyName),
        leading: Icon(icon),
        title: Text(title),
        subtitle: Text(text),
        trailing: const Icon(Icons.chevron_right),
        onTap: onTap,
      ),
    );
  }
}
