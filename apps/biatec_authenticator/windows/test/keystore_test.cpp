// T-250: a console program that runs the real key store (../runner/keystore.cpp) against this computer. It is not part of the app and not of `flutter test`.
//
// It MAKES AND DELETES the keys of the app for the signed-in Windows user, so it refuses to start on a computer that is enrolled (pass --force to run anyway
// and lose that enrollment).
//
// Build and run from an "x64 Native Tools Command Prompt for VS", in this folder:
//   cl /nologo /EHsc /W4 /WX /std:c++17 keystore_test.cpp ncrypt.lib bcrypt.lib crypt32.lib /Fe:keystore_tpm.exe && keystore_tpm.exe
//   cl /nologo /EHsc /W4 /WX /std:c++17 /DBIATEC_KEYSTORE_WITHOUT_TPM keystore_test.cpp ncrypt.lib bcrypt.lib crypt32.lib /Fe:keystore_soft.exe && keystore_soft.exe
// The first uses the TPM when the computer has one; the second is the software store, as on a computer without a TPM.
#include "../runner/keystore.cpp"

#include <cstdio>

using namespace keystore;

static int failed = 0;
static void Check(bool ok, const char* what) {
  printf("  %s %s\n", ok ? "ok  " : "FAIL", what);
  if (!ok) failed++;
}

static bool Verify(const PublicKey& key, const std::vector<uint8_t>& data, const std::vector<uint8_t>& sig) {
  auto unb64 = [](const std::string& s) {
    std::vector<uint8_t> out;
    uint32_t v = 0;
    int bits = 0;
    for (char c : s) {
      int d = c >= 'A' && c <= 'Z' ? c - 'A' : c >= 'a' && c <= 'z' ? c - 'a' + 26 : c >= '0' && c <= '9' ? c - '0' + 52 : c == '-' ? 62 : 63;
      v = (v << 6) | d;
      bits += 6;
      if (bits >= 8) { bits -= 8; out.push_back((uint8_t)(v >> bits)); }
    }
    return out;
  };
  std::vector<uint8_t> blob(sizeof(BCRYPT_ECCKEY_BLOB) + 64);
  BCRYPT_ECCKEY_BLOB header = {BCRYPT_ECDSA_PUBLIC_P256_MAGIC, 32};
  memcpy(blob.data(), &header, sizeof(header));
  auto x = unb64(key.x), y = unb64(key.y);
  if (x.size() != 32 || y.size() != 32) return false;
  memcpy(blob.data() + sizeof(header), x.data(), 32);
  memcpy(blob.data() + sizeof(header) + 32, y.data(), 32);
  BCRYPT_KEY_HANDLE pub = 0;
  if (BCryptImportKeyPair(BCRYPT_ECDSA_P256_ALG_HANDLE, nullptr, BCRYPT_ECCPUBLIC_BLOB, &pub, blob.data(), (ULONG)blob.size(), 0) != 0) return false;
  uint8_t digest[32];
  BCryptHash(BCRYPT_SHA256_ALG_HANDLE, nullptr, 0, (PUCHAR)data.data(), (ULONG)data.size(), digest, 32);
  const NTSTATUS s = BCryptVerifySignature(pub, nullptr, digest, 32, (PUCHAR)sig.data(), (ULONG)sig.size(), 0);
  BCryptDestroyKey(pub);
  return s == 0;
}

static std::string CodeOf(void (*f)()) {
  try { f(); } catch (const KeyError& e) { return e.code(); }
  return "";
}

int main(int argc, char** argv) {
  const bool force = argc > 1 && std::string(argv[1]) == "--force";
  try {
    const Existing before = Exists();
    if ((before.approval || before.presence || before.lock) && !force) {
      printf("This Windows user has keys of the app (an enrollment). Not run: it would delete them. Pass --force to run anyway.\n");
      return 2;
    }
  } catch (const KeyError& e) {
    printf("The key store cannot be read (%s). Not run.\n", e.code().c_str());
    return 2;
  }
  const std::vector<uint8_t> data = {1, 2, 3, 4};
  const std::wstring pin = L"246810", wrong = L"000000";
  try {
    const bool tpm = TpmAvailable();
    printf("TpmAvailable: %d\n", tpm);
    const Store expected = tpm ? Store::kTpm : Store::kSoftware;

    printf("-- no PIN (Windows Hello)\n");
    KeyPair pair = CreateFreshPair();
    Check(pair.approval.store == expected && pair.presence.store == expected, "the store is the expected one");
    Check(pair.approval.hardware == tpm && pair.presence.hardware == tpm, "hardware is reported only for the TPM");
    Existing e = Exists();
    Check(e.approval && e.presence && !e.lock, "approval and presence, no lock key");
    Check(Verify(pair.approval, data, Sign(Use::kApproval, data)), "the approval signature verifies");
    Check(Verify(pair.presence, data, Sign(Use::kPresence, data)), "the presence signature verifies");
    auto loaded = Load(Use::kApproval);
    Check(loaded && loaded->x == pair.approval.x && loaded->store == expected && loaded->hardware == tpm, "load gives the same key and level");
    Check(CodeOf([] { CheckPin(L"246810"); }) == "key_missing", "no lock key: key_missing");

    printf("-- with a PIN\n");
    KeyPair second = CreateFreshPair(&pin);
    Check(second.approval.x != pair.approval.x, "a new pair, not the old one");
    e = Exists();
    Check(e.approval && e.presence && e.lock, "all three keys");
    Check(CheckPin(pin) == PinCheck::kAccepted, "the PIN is accepted");
    Check(CheckPin(wrong) == PinCheck::kRefused, "a wrong PIN is refused");
    Check(CheckPin(pin) == PinCheck::kAccepted, "the PIN is accepted after a wrong one");
    Check(Verify(second.approval, data, Sign(Use::kApproval, data, &pin)), "the approval signature with the PIN verifies");
    if (tpm) {
      Check(CodeOf([] { Sign(Use::kApproval, {1}, nullptr); }) == "sign_failed", "TPM: no signature without the PIN");
      Check(CodeOf([] { const std::wstring w = L"000000"; Sign(Use::kApproval, {1}, &w); }) == "auth_failed", "TPM: no signature with a wrong PIN");
    } else {
      PinRecord record;
      Check(ReadPinRecord(&record) && record.failures == 0, "software: the record is there, the count is back at 0");
      for (int i = 0; i < 5; i++) Check(CheckPin(wrong) == PinCheck::kRefused, "software: wrong try");
      Check(CheckPin(pin) == PinCheck::kLocked, "software: locked after 5 wrong tries, also for the right PIN");
      Check(ReadPinRecord(&record) && record.failures == 5, "software: 5 failures kept");
      record.last_failure -= 31ULL * 10000000ULL;
      WritePinRecord(record);
      Check(CheckPin(pin) == PinCheck::kAccepted, "software: accepted after the wait");
      Check(WaitSeconds(4) == 0 && WaitSeconds(5) == 30 && WaitSeconds(6) == 60 && WaitSeconds(40) == 3600, "software: the waits");
    }
    Check(Verify(second.presence, data, Sign(Use::kPresence, data)), "the presence key needs no PIN");

    printf("-- single key mode with a PIN\n");
    PublicKey presence = CreateFreshPresence(&pin);
    e = Exists();
    Check(!e.approval && e.presence && e.lock, "presence and lock, no approval key");
    Check(CheckPin(pin) == PinCheck::kAccepted && CheckPin(wrong) == PinCheck::kRefused, "the PIN check");
    Check(Verify(presence, data, Sign(Use::kPresence, data)), "the presence signature verifies");

    printf("-- delete\n");
    DeleteAll();
    e = Exists();
    Check(!e.approval && !e.presence && !e.lock, "nothing left");
    PinRecord gone;
    Check(!ReadPinRecord(&gone), "no PIN record left");
    Check(!Load(Use::kPresence), "load gives nothing");
  } catch (const KeyError& e) {
    printf("FAIL KeyError %s: %s\n", e.code().c_str(), e.what());
    failed++;
    try { DeleteAll(); } catch (...) {}
  }
  printf(failed ? "FAILED: %d\n" : "ALL OK\n", failed);
  return failed;
}
