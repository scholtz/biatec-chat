<script setup lang="ts">
import { computed, onMounted } from "vue";
import ImpersonationBanner from "./components/ImpersonationBanner.vue";
import SignedOut from "./components/SignedOut.vue";
import Overview from "./views/Overview.vue";
import UserPage from "./views/UserPage.vue";
import Users from "./views/Users.vue";
import ViewAs from "./views/ViewAs.vue";
import { route } from "./router";
import { refreshSession, session, setImpersonation, signOut } from "./session";

// ?error=<code> from a failed sign-in (query before the hash).
const loginError = new URLSearchParams(typeof location === "undefined" ? "" : location.search).get("error");

onMounted(refreshSession);

const nav = [
  { name: "overview", href: "#/", label: "Overview" },
  { name: "users", href: "#/users", label: "Users" },
  { name: "view", href: "#/view", label: "Impersonation" },
] as const;
const active = computed(() => (route.value.name === "user" ? "users" : route.value.name));
const view = computed(() => ({ overview: Overview, users: Users, user: UserPage, view: ViewAs })[route.value.name]);
</script>

<template>
  <main v-if="session.status === 'loading'" class="center-screen" aria-live="polite">
    <p class="muted">Loading…</p>
  </main>
  <main v-else-if="session.status === 'error'" class="center-screen">
    <div class="card signin" role="alert">
      <h1>Something went wrong</h1>
      <p class="muted">{{ session.error }}</p>
      <button type="button" class="btn btn-primary" @click="refreshSession">Retry</button>
    </div>
  </main>
  <SignedOut v-else-if="session.status === 'out' || !session.me" :error="loginError" />
  <div v-else class="shell">
    <a class="skip" href="#main">Skip to content</a>
    <header class="topbar">
      <div class="brand"><span class="brand-mark" aria-hidden="true">B</span> Biatec Admin</div>
      <div class="topbar-right">
        <span class="who" data-test="admin-email">{{ session.me.email }}</span>
        <button type="button" class="btn btn-small" data-test="signout" @click="signOut">Sign out</button>
      </div>
    </header>
    <ImpersonationBanner
      v-if="session.me.impersonation"
      :impersonation="session.me.impersonation"
      @stopped="setImpersonation(null)"
      @expired="refreshSession"
    />
    <div class="body">
      <nav class="sidenav" aria-label="Main">
        <a v-for="n in nav" :key="n.name" :href="n.href" :aria-current="active === n.name ? 'page' : undefined">{{ n.label }}</a>
      </nav>
      <main id="main" class="content" tabindex="-1">
        <component :is="view" :key="route.name + route.query.toString()" />
      </main>
    </div>
  </div>
</template>
