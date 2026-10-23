<script setup lang="ts">
import { computed } from "vue";

const props = defineProps<{ error?: string | null }>();

// Plain messages only; nothing beyond these codes is ever shown (the backend must not reveal the allow-list).
const MESSAGES: Record<string, string> = {
  not_admin: "This account is not allowed to use the dashboard.",
  email_not_verified: "Your email address is not verified, so it cannot be used to sign in here.",
  login_failed: "Sign-in did not complete. Please try again.",
  access_denied: "Sign-in was cancelled or denied.",
};

const message = computed(() => (props.error ? (MESSAGES[props.error] ?? null) : null));

function signIn() {
  location.assign("auth/login");
}
</script>

<template>
  <main class="center-screen">
    <section class="card signin" aria-labelledby="signin-title">
      <div class="brand brand-large"><span class="brand-mark" aria-hidden="true">B</span> Biatec Admin</div>
      <h1 id="signin-title">Sign in</h1>
      <p class="muted">Administrators sign in with their Biatec account.</p>
      <p v-if="message" class="notice notice-error" role="alert" data-test="signin-error">{{ message }}</p>
      <button type="button" class="btn btn-primary btn-block" data-test="signin" @click="signIn">Sign in with Biatec</button>
    </section>
  </main>
</template>
