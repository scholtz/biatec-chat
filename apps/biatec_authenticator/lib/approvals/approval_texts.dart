import 'package:material_ui/material_ui.dart';

import '../l10n/texts.dart';

/// The texts of the approvals (T-251): the pending list, the approval screen and its card, the binding code, the time left, and the messages the
/// approval controller and client make for those screens. English and Slovak; see [Texts].
class ApprovalTexts extends Texts {
  const ApprovalTexts._(super.values);

  factory ApprovalTexts.of([Locale? locale]) =>
      appLanguageIsSlovak(locale) ? sk : en;

  /// The language of the widget tree at [context] (the device's language when there is none).
  factory ApprovalTexts.ofContext(BuildContext context) =>
      ApprovalTexts.of(Localizations.maybeLocaleOf(context));

  /// Whether [message] is the text of [key] in either language, or starts with it up to its first `{placeholder}`. A screen recognises the messages
  /// the controller made this way, whatever language they were made in.
  static bool isMessage(String message, String key) {
    for (final texts in const [en, sk]) {
      final value = texts.values[key]!;
      final cut = value.indexOf('{');
      if (cut < 0 ? message == value : message.startsWith(value.substring(0, cut))) {
        return true;
      }
    }
    return false;
  }

  static const en = ApprovalTexts._({
    // The pending list.
    'pendingTitle': 'Pending approvals',
    'refresh': 'Refresh',
    'loadFailed': 'Could not load requests',
    'tryAgain': 'Try again',
    'offlineTitle': 'You are offline',
    'notEnrolledTitle': 'This device is not enrolled',
    'notEnrolledBody': 'Enroll this device to receive requests.',
    'noPendingTitle': 'No pending approvals',
    'noPendingBody':
        'Requests from Biatec Chat appear here, and this device is notified when one arrives.',
    'checkAgain': 'Check again',
    // A request in the list.
    'onNetwork': '{value} on {network}',
    'changeLimits': 'Change spending limits',
    'paymentRequest': 'Payment request',
    'signIn': 'Sign in',
    'signInRequest': 'Sign-in request',
    'actionRequested': 'Action requested',
    'confirmActionRequest': 'Request to confirm an action',
    'cannotShowTitle': 'This request cannot be shown',
    'updateApp': 'Update the app',
    'cardSpoken': 'Request from {who}: {headline}, {sub}, {left}.',
    'cardSpokenQuoted': '{from} written by the service: {quoted}.',
    'unprovenSpoken': 'Unproven application.',
    'doubleTapOpen': 'Double tap to open.',
    'unproven': 'Unproven',
    // The approval screen.
    'approvalTitle': 'Approval',
    'notAvailable': 'This request is not available.',
    'notAvailableTitle': 'This request is not available',
    'alreadyDecidedTitle': 'Already decided',
    'backToApprovals': 'Back to approvals',
    'offlineNeedsOidc': 'Offline. Approvals need a connection to Biatec OIDC.',
    'approvedTitle': 'Approved.',
    'approvedBody':
        'Your approval was sent to Biatec OIDC, which continues the action. You can close this screen.',
    'done': 'Done',
    'deniedTitle': 'Denied.',
    'deniedBody': 'Nothing was approved. Your decision was sent to Biatec OIDC.',
    'codeFailedTitle': 'Three wrong codes. The request was refused.',
    'codeFailedBody':
        'Start the action again to get a new request and a new code.',
    'expiredBody':
        'Nothing was approved. Start the action again to get a new request.',
    'offlineBanner':
        'Offline. Nothing was approved. {error} Approve and Deny are off until Biatec OIDC can be reached again.',
    'expiredBannerTitle': 'This request has expired',
    'expiredBanner':
        'Approve and Deny are off: this request can no longer be approved or denied. Go back and start the action again.',
    'keyInvalidatedTitle': 'Approvals are off on this phone',
    'keyInvalidatedBody': '{message} Deny still works.',
    'keysMissingTitle': 'This phone has no device keys',
    'keysMissingBody': '{message} It cannot approve or deny until then.',
    'enrollAgain': 'Enroll this device again',
    'touchObscured':
        'Another app is drawing over this screen, so the approval was not sent. Close it (a chat bubble or a screen filter) and tap Approve again.',
    'errorWrongCodeTitle': 'Wrong code',
    'errorChangedTitle': 'The request changed',
    'errorCancelledTitle': 'Approval cancelled',
    'errorTitle': 'Could not complete',
    'canTryAgain': '{error} You can try again.',
    // The card.
    'messageFrom': 'Message from {domain}',
    'originByApplication': 'Origin, as reported by the application',
    'valueUnknown': 'Value unknown',
    'valueUnknownBody':
        'Biatec could not work out what this is worth, or an application in it is not trusted. Approve only if you started this action yourself.',
    'transfers': 'Transfers',
    'none': 'None',
    'from': 'From',
    'network': 'Network',
    'groupHash': 'Group hash',
    'to': 'To',
    'assetLine': 'Asset {symbol} (id {id}, {decimals} decimals)',
    'callsApplication': 'Calls application',
    'callsApplicationId': 'Calls application {id}',
    'untrustedApplication':
        'You have not trusted this application for this service. It can do more than the transfers listed.',
    'perTransaction': 'Per transaction',
    'perDay': 'Per day',
    'requestHash': 'Request hash',
    'limitsRaisedTitle': 'This raises your limits',
    'limitsRaisedBody':
        'More can be moved without asking you again. Approve only if you asked for this.',
    'limitsLoweredTitle': 'This lowers your limits',
    'limitsLoweredBody': 'Less can be moved without asking you.',
    'limitsSameTitle': 'These limits stay the same',
    'limitsSameBody': 'The requested limits equal the current ones.',
    'youAreApproving': 'You are approving',
    'youAreApprovingSpoken': 'You are approving {amount}, worth {fiat}',
    'noTransfers': 'No transfers',
    'nTransfers': '{n} transfers',
    // The buttons and the code.
    'approve': 'Approve',
    'deny': 'Deny',
    'working': 'Working',
    'codeLabel': 'Code shown where you started this action',
    'threeAttempts': 'Three attempts in total',
    'oneAttemptLeft': '1 attempt left',
    'attemptsLeft': '{n} attempts left',
    'codeWarning':
        'It must match the screen where you started this action. Never enter a code that someone sends you.',
    // The time left.
    'expiredShort': 'Expired',
    'lessThanMinuteLeft': 'Less than a minute left',
    'minutesLeft': '{n} min left',
    'spokenExpired': 'expired',
    'spokenLessThanMinute': 'expires in less than a minute',
    'spokenOneMinute': 'expires in 1 minute',
    'spokenFewMinutes': 'expires in {n} minutes',
    'spokenMinutes': 'expires in {n} minutes',
    'requestExpiredSpoken': 'Request expired',
    'requestLeftSpoken': 'Request {left}',
    'expiresIn': 'Expires in {clock}',
    // What the controller says.
    'notEnrolled': 'This device is not enrolled.',
    'expired': 'This request has expired.',
    'noLongerPending':
        'This request is no longer pending. It was already decided.',
    'alreadyDecided': 'This request was already decided.',
    'notActiveYet': 'This device is not active yet, so it cannot decide.',
    'keyMissing': 'The device key is missing. Enroll this device again.',
    'noAttestation':
        'This enrollment has no attestation. Enroll the device again.',
    'oldKey': 'This device uses an old device key. Enroll it again.',
    'cannotProtect':
        'This device cannot protect approvals. Remove it from your account and enroll again on a device that can.',
    'enterCode': 'Enter the six-digit code.',
    'promptTitle': 'Approve this action',
    'biometricFailed': 'Biometric check did not pass.',
    'requestChanged': 'The request changed. Review it again before approving.',
    'approvalCancelled': 'Approval cancelled. Nothing was sent.',
    'couldNotApprove': 'Could not approve: {why}',
    'couldNotDeny': 'Could not deny: {why}',
    'couldNotSign': 'Could not sign: {why}',
    'wrongCode': 'Wrong code.',
    'wrongCodeOne': 'Wrong code. 1 attempt left.',
    'wrongCodeN': 'Wrong code. {n} attempts left.',
    'codeFailedNotAccepted': 'The code_failed receipt was not accepted.',
    'refused': 'Refused ({status}): {why}',
    'unexpectedAnswer': 'Unexpected answer from Biatec OIDC.',
    // One line for the list, the history and the biometric prompt.
    'summaryTransaction': '{headline} ({value}) on {network}',
    'summaryLimits':
        'Limits to {perTransaction} EUR per transaction, {perDay} EUR per day',
    'summaryPayment': 'Payment: {amount} {currency}',
    'summaryCannotShow': 'Request that cannot be shown',
    'promptPayment': 'Payment of {amount} {currency}',
    'promptAction': 'Confirm an action',
    // What the client says.
    'proofFailed': 'The device proof could not be made: {why}',
    'unreachable': 'Biatec OIDC is not reachable: {why}',
    'unexpectedResponse': 'Unexpected response ({status}).',
    'loadRequestsFailed': 'Could not load requests ({status}): {why}',
    'malformedList': 'Malformed request list.',
    'loadRequestFailed': 'Could not load the request ({status}): {why}',
    'cannotShowRequest': 'The request cannot be shown: {why}',
    'noAnswerReceipt':
        'No answer from Biatec OIDC, so the decision may or may not be recorded. Reopen the request to check. ({why})',
    'malformedReceipt': 'Malformed receipt response.',
    'noAnswerLink':
        'No answer from Biatec OIDC, so the decision may or may not be recorded. Check Linked services in Settings. ({why})',
    'malformedLink': 'Malformed link response.',
    'loadLinkFailed': 'Could not load the link ({status}): {why}',
  });

  static const sk = ApprovalTexts._({
    'pendingTitle': 'Čakajúce schválenia',
    'refresh': 'Obnoviť',
    'loadFailed': 'Žiadosti sa nepodarilo načítať',
    'tryAgain': 'Skúsiť znova',
    'offlineTitle': 'Ste offline',
    'notEnrolledTitle': 'Toto zariadenie nie je zaregistrované',
    'notEnrolledBody': 'Zaregistrujte toto zariadenie, aby vám prichádzali žiadosti.',
    'noPendingTitle': 'Žiadne čakajúce schválenia',
    'noPendingBody':
        'Tu sa zobrazujú žiadosti z Biatec Chat a toto zariadenie dostane upozornenie, keď nejaká príde.',
    'checkAgain': 'Skontrolovať znova',
    'onNetwork': '{value} v sieti {network}',
    'changeLimits': 'Zmena limitov výdavkov',
    'paymentRequest': 'Žiadosť o platbu',
    'signIn': 'Prihlásenie',
    'signInRequest': 'Žiadosť o prihlásenie',
    'actionRequested': 'Žiadosť o akciu',
    'confirmActionRequest': 'Žiadosť o potvrdenie akcie',
    'cannotShowTitle': 'Túto žiadosť nemožno zobraziť',
    'updateApp': 'Aktualizujte aplikáciu',
    'cardSpoken': 'Žiadosť od {who}: {headline}, {sub}, {left}.',
    'cardSpokenQuoted': '{from} napísala služba: {quoted}.',
    'unprovenSpoken': 'Neoverená aplikácia.',
    'doubleTapOpen': 'Dvojitým ťuknutím otvoríte.',
    'unproven': 'Neoverená',
    'approvalTitle': 'Schválenie',
    'notAvailable': 'Táto žiadosť nie je dostupná.',
    'notAvailableTitle': 'Táto žiadosť nie je dostupná',
    'alreadyDecidedTitle': 'Už rozhodnuté',
    'backToApprovals': 'Späť na schválenia',
    'offlineNeedsOidc': 'Offline. Schvaľovanie potrebuje spojenie s Biatec OIDC.',
    'approvedTitle': 'Schválené.',
    'approvedBody':
        'Vaše schválenie sa odoslalo do Biatec OIDC, ktorý v akcii pokračuje. Túto obrazovku môžete zavrieť.',
    'done': 'Hotovo',
    'deniedTitle': 'Zamietnuté.',
    'deniedBody': 'Nič sa neschválilo. Vaše rozhodnutie sa odoslalo do Biatec OIDC.',
    'codeFailedTitle': 'Tri nesprávne kódy. Žiadosť bola odmietnutá.',
    'codeFailedBody':
        'Spustite akciu znova, dostanete novú žiadosť a nový kód.',
    'expiredBody':
        'Nič sa neschválilo. Spustite akciu znova, dostanete novú žiadosť.',
    'offlineBanner':
        'Offline. Nič sa neschválilo. {error} Schváliť a Zamietnuť sú vypnuté, kým nebude Biatec OIDC opäť dostupný.',
    'expiredBannerTitle': 'Platnosť tejto žiadosti vypršala',
    'expiredBanner':
        'Schváliť a Zamietnuť sú vypnuté: túto žiadosť už nemožno schváliť ani zamietnuť. Vráťte sa späť a spustite akciu znova.',
    'keyInvalidatedTitle': 'Schvaľovanie je v tomto telefóne vypnuté',
    'keyInvalidatedBody': '{message} Zamietnuť stále môžete.',
    'keysMissingTitle': 'Tento telefón nemá kľúče zariadenia',
    'keysMissingBody': '{message} Dovtedy nemôže schvaľovať ani zamietať.',
    'enrollAgain': 'Znova zaregistrovať toto zariadenie',
    'touchObscured':
        'Cez túto obrazovku kreslí iná aplikácia, preto sa schválenie neodoslalo. Zatvorte ju (bublinu chatu alebo filter obrazovky) a znova ťuknite na Schváliť.',
    'errorWrongCodeTitle': 'Nesprávny kód',
    'errorChangedTitle': 'Žiadosť sa zmenila',
    'errorCancelledTitle': 'Schválenie zrušené',
    'errorTitle': 'Nepodarilo sa dokončiť',
    'canTryAgain': '{error} Môžete to skúsiť znova.',
    'messageFrom': 'Správa od {domain}',
    'originByApplication': 'Pôvod, ako ho uviedla aplikácia',
    'valueUnknown': 'Hodnota neznáma',
    'valueUnknownBody':
        'Biatec nevedel zistiť, akú to má hodnotu, alebo niektorej aplikácii v tom nedôverujete. Schváľte iba vtedy, ak ste túto akciu spustili sami.',
    'transfers': 'Prevody',
    'none': 'Žiadne',
    'from': 'Od',
    'network': 'Sieť',
    'groupHash': 'Hash skupiny',
    'to': 'Komu',
    'assetLine': 'Aktívum {symbol} (id {id}, desatinné miesta: {decimals})',
    'callsApplication': 'Volá aplikáciu',
    'callsApplicationId': 'Volá aplikáciu {id}',
    'untrustedApplication':
        'Tejto aplikácii ste pri tejto službe nedôverovali. Môže urobiť viac než uvedené prevody.',
    'perTransaction': 'Za transakciu',
    'perDay': 'Za deň',
    'requestHash': 'Hash žiadosti',
    'limitsRaisedTitle': 'Týmto sa vaše limity zvyšujú',
    'limitsRaisedBody':
        'Bez ďalšieho pýtania sa bude dať presunúť viac. Schváľte iba vtedy, ak ste o to požiadali.',
    'limitsLoweredTitle': 'Týmto sa vaše limity znižujú',
    'limitsLoweredBody': 'Bez pýtania sa bude dať presunúť menej.',
    'limitsSameTitle': 'Tieto limity zostávajú rovnaké',
    'limitsSameBody': 'Požadované limity sa rovnajú súčasným.',
    'youAreApproving': 'Schvaľujete',
    'youAreApprovingSpoken': 'Schvaľujete {amount} v hodnote {fiat}',
    'noTransfers': 'Žiadne prevody',
    'nTransfers': 'Prevody: {n}',
    'approve': 'Schváliť',
    'deny': 'Zamietnuť',
    'working': 'Prebieha',
    'codeLabel': 'Kód zobrazený tam, kde ste túto akciu spustili',
    'threeAttempts': 'Spolu tri pokusy',
    'oneAttemptLeft': 'Zostáva 1 pokus',
    'attemptsLeft': 'Zostáva pokusov: {n}',
    'codeWarning':
        'Musí sa zhodovať s obrazovkou, na ktorej ste túto akciu spustili. Nikdy nezadávajte kód, ktorý vám niekto pošle.',
    'expiredShort': 'Vypršala',
    'lessThanMinuteLeft': 'Zostáva menej ako minúta',
    'minutesLeft': 'Zostáva {n} min',
    'spokenExpired': 'vypršala',
    'spokenLessThanMinute': 'vyprší o menej ako minútu',
    'spokenOneMinute': 'vyprší o 1 minútu',
    'spokenFewMinutes': 'vyprší o {n} minúty',
    'spokenMinutes': 'vyprší o {n} minút',
    'requestExpiredSpoken': 'Platnosť žiadosti vypršala',
    'requestLeftSpoken': 'Žiadosť {left}',
    'expiresIn': 'Vyprší o {clock}',
    'notEnrolled': 'Toto zariadenie nie je zaregistrované.',
    'expired': 'Platnosť tejto žiadosti vypršala.',
    'noLongerPending':
        'Táto žiadosť už nečaká na rozhodnutie. Už bola rozhodnutá.',
    'alreadyDecided': 'O tejto žiadosti už bolo rozhodnuté.',
    'notActiveYet':
        'Toto zariadenie ešte nie je aktívne, preto nemôže rozhodovať.',
    'keyMissing': 'Kľúč zariadenia chýba. Znova zaregistrujte toto zariadenie.',
    'noAttestation':
        'Táto registrácia nemá atestáciu. Zaregistrujte zariadenie znova.',
    'oldKey': 'Toto zariadenie používa starý kľúč zariadenia. Zaregistrujte ho znova.',
    'cannotProtect':
        'Toto zariadenie nedokáže ochrániť schválenia. Odstráňte ho zo svojho účtu a zaregistrujte sa znova na zariadení, ktoré to dokáže.',
    'enterCode': 'Zadajte šesťmiestny kód.',
    'promptTitle': 'Schváliť túto akciu',
    'biometricFailed': 'Biometrické overenie neprešlo.',
    'requestChanged': 'Žiadosť sa zmenila. Pred schválením ju znova skontrolujte.',
    'approvalCancelled': 'Schválenie sa zrušilo. Nič sa neodoslalo.',
    'couldNotApprove': 'Nepodarilo sa schváliť: {why}',
    'couldNotDeny': 'Nepodarilo sa zamietnuť: {why}',
    'couldNotSign': 'Nepodarilo sa podpísať: {why}',
    'wrongCode': 'Nesprávny kód.',
    'wrongCodeOne': 'Nesprávny kód. Zostáva 1 pokus.',
    'wrongCodeN': 'Nesprávny kód. Zostáva pokusov: {n}.',
    'codeFailedNotAccepted': 'Potvrdenie code_failed nebolo prijaté.',
    'refused': 'Odmietnuté ({status}): {why}',
    'unexpectedAnswer': 'Neočakávaná odpoveď od Biatec OIDC.',
    'summaryTransaction': '{headline} ({value}) v sieti {network}',
    'summaryLimits':
        'Limity na {perTransaction} EUR za transakciu, {perDay} EUR za deň',
    'summaryPayment': 'Platba: {amount} {currency}',
    'summaryCannotShow': 'Žiadosť, ktorú nemožno zobraziť',
    'promptPayment': 'Platba {amount} {currency}',
    'promptAction': 'Potvrďte akciu',
    'proofFailed': 'Dôkaz zariadenia sa nepodarilo vytvoriť: {why}',
    'unreachable': 'Biatec OIDC nie je dostupný: {why}',
    'unexpectedResponse': 'Neočakávaná odpoveď ({status}).',
    'loadRequestsFailed': 'Žiadosti sa nepodarilo načítať ({status}): {why}',
    'malformedList': 'Zoznam žiadostí má chybný formát.',
    'loadRequestFailed': 'Žiadosť sa nepodarilo načítať ({status}): {why}',
    'cannotShowRequest': 'Žiadosť nemožno zobraziť: {why}',
    'noAnswerReceipt':
        'Biatec OIDC neodpovedal, preto rozhodnutie možno je, a možno nie je zaznamenané. Otvorte žiadosť znova a skontrolujte to. ({why})',
    'malformedReceipt': 'Odpoveď na potvrdenie má chybný formát.',
    'noAnswerLink':
        'Biatec OIDC neodpovedal, preto rozhodnutie možno je, a možno nie je zaznamenané. Skontrolujte Prepojené služby v Nastaveniach. ({why})',
    'malformedLink': 'Odpoveď na prepojenie má chybný formát.',
    'loadLinkFailed': 'Prepojenie sa nepodarilo načítať ({status}): {why}',
  });
}
