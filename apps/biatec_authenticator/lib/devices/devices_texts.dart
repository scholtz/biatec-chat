import 'dart:ui';

import '../l10n/texts.dart';

/// The texts of Device management (T-251). English and Slovak. The activation by pairing code is in `EnrollStrings`.
class DevicesTexts extends Texts {
  const DevicesTexts._(super.values);

  /// The texts for [locale] (the phone's language when null).
  factory DevicesTexts.of([Locale? locale]) =>
      appLanguageIsSlovak(locale) ? sk : en;

  static const en = DevicesTexts._({
    'title': 'Device management',
    'noDevices': 'No devices are enrolled.',
    'inUseTitle': 'Devices that are in use stay',
    'inUseBody':
        'Only an enrollment that is still pending can be cancelled here. A device that is in use cannot be removed from this app.',
    'cancelHereTitle': 'Cancel the enrollment of this device?',
    'cancelOtherTitle': 'Cancel this enrollment?',
    'cancelHereBody':
        'This is the device you are using. Cancelling its pending enrollment signs it out of this account: it can no longer approve anything, and you will have to enroll it again from the start.',
    'cancelOtherBody':
        'The device is removed and can no longer approve anything. You can enroll it again later.',
    'cancelHereWarning': 'You are cancelling the device in your hand.',
    'cancelEnrollment': 'Cancel enrollment',
    'keep': 'Keep',
    'thisDevice': 'This device',
    'pending': 'Pending',
    'active': 'Active',
    'kid': 'Device key id',
    'pendingUntil':
        'Pending. It becomes active {when}. Until then it approves nothing.',
    'keyElsewhere': 'Where its key is kept is known only on the device itself.',
    'levelUnknownTitle': 'Key protection unknown',
    'levelUnknownBody': "This device's keystore did not say where its key is kept.",
  });

  static const sk = DevicesTexts._({
    'title': 'Správa zariadení',
    'noDevices': 'Nie je zaregistrované žiadne zariadenie.',
    'inUseTitle': 'Používané zariadenia zostávajú',
    'inUseBody':
        'Zrušiť sa tu dá iba registrácia, ktorá ešte čaká. Zariadenie, ktoré sa používa, sa z tejto aplikácie odstrániť nedá.',
    'cancelHereTitle': 'Zrušiť registráciu tohto zariadenia?',
    'cancelOtherTitle': 'Zrušiť túto registráciu?',
    'cancelHereBody':
        'Toto je zariadenie, ktoré práve používate. Zrušením jeho čakajúcej registrácie sa odhlási z tohto účtu: už nebude môcť nič schvaľovať a budete ho musieť zaregistrovať odznova.',
    'cancelOtherBody':
        'Zariadenie sa odstráni a už nebude môcť nič schvaľovať. Neskôr ho môžete zaregistrovať znova.',
    'cancelHereWarning': 'Rušíte zariadenie, ktoré držíte v ruke.',
    'cancelEnrollment': 'Zrušiť registráciu',
    'keep': 'Ponechať',
    'thisDevice': 'Toto zariadenie',
    'pending': 'Čaká',
    'active': 'Aktívne',
    'kid': 'ID kľúča zariadenia',
    'pendingUntil':
        'Čaká. Aktívne bude {when}. Dovtedy nič neschvaľuje.',
    'keyElsewhere': 'Kde je uložený jeho kľúč, vie iba samotné zariadenie.',
    'levelUnknownTitle': 'Ochrana kľúča neznáma',
    'levelUnknownBody':
        'Úložisko kľúčov tohto zariadenia neuviedlo, kde je jeho kľúč uložený.',
  });
}
