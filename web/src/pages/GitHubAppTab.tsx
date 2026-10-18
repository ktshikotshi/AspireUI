import { useEffect, useState } from "react";
import { Alert, Anchor, Badge, Button, Code, Group, Loader, NumberInput, Stack as MStack, Switch, Text, TextInput } from "@mantine/core";
import { IconAlertCircle, IconBrandGithub, IconExternalLink } from "@tabler/icons-react";
import * as api from "../api";
import { confirmAction, toastErr, toastOk } from "../ui";

// AspireUI as a GitHub App. Created from a manifest — GitHub generates the keys and hands them over,
// nobody pastes a private key — then installed on the repositories it may deploy.
export function GitHubAppTab() {
  const [s, setS] = useState<api.GitHubAppStatus | null>(null);
  const [baseUrl, setBaseUrl] = useState(window.location.origin);
  const [name, setName] = useState("");
  const [org, setOrg] = useState("");
  const [webhook, setWebhook] = useState(false);
  const [poll, setPoll] = useState<number | "">(5);
  const [login, setLogin] = useState(false);
  const [busy, setBusy] = useState(false);
  const returned = new URLSearchParams(window.location.search).get("github");

  const load = () => api.githubApp().then(r => { setS(r); setPoll(r.pollMinutes); setLogin(r.loginEnabled); }).catch(toastErr);
  useEffect(() => { load(); }, []);
  if (!s) return <Loader size="sm" />;

  const save = async () => {
    setBusy(true);
    try { await api.setGitHubSettings({ loginEnabled: login, pollMinutes: Number(poll) || 0 }); toastOk("Saved"); await load(); }
    catch (e) { toastErr(e); }
    finally { setBusy(false); }
  };
  const forget = async () => {
    if (!await confirmAction("Forget the GitHub App?",
      "AspireUI stops using it here. The app stays on GitHub until you delete it there.", "Forget")) return;
    await api.forgetGitHubApp().catch(toastErr);
    await load();
  };

  return (
    <MStack gap="md" maw={620}>
      <Text size="sm" c="dimmed">
        One GitHub App for this instance. It clones private repositories with short-lived tokens instead of a
        pasted access token, redeploys an app when its branch gets a push, and can sign people in.
      </Text>
      {returned && (
        <Alert color="red" variant="light" icon={<IconAlertCircle size={16} />}>{returned}</Alert>
      )}

      {!s.configured ? (
        <>
          <TextInput label="Address of this AspireUI" value={baseUrl} onChange={e => setBaseUrl(e.currentTarget.value)}
            description="GitHub sends people back here after creating the app and signing in, so it is the address your browser uses." />
          <Group grow>
            <TextInput label="App name" placeholder={`AspireUI ${window.location.hostname}`} value={name}
              onChange={e => setName(e.currentTarget.value)} description="Unique across GitHub." />
            <TextInput label="Organization" placeholder="your account" value={org}
              onChange={e => setOrg(e.currentTarget.value)} description="Blank creates it under your own account." />
          </Group>
          <Switch checked={webhook} onChange={e => setWebhook(e.currentTarget.checked)}
            label="GitHub can reach this address from the internet"
            description="Turns on the push webhook. Off, AspireUI checks for new commits every few minutes instead." />
          <Group>
            <Button leftSection={<IconBrandGithub size={16} />} component="a"
              href={api.githubAppNewUrl({ baseUrl, name: name.trim() || undefined, org: org.trim() || undefined, webhook })}>
              Create GitHub App
            </Button>
          </Group>
          <Text size="xs" c="dimmed">
            GitHub asks you to confirm, then to pick the repositories to install it on.
          </Text>
        </>
      ) : (
        <>
          <Group gap="xs">
            <IconBrandGithub size={18} />
            <Anchor href={s.htmlUrl} target="_blank" fw={500}>{s.slug} <IconExternalLink size={11} /></Anchor>
            <Text size="sm" c="dimmed">owned by {s.owner}</Text>
          </Group>
          {s.error && <Alert color="red" variant="light" icon={<IconAlertCircle size={16} />}>{s.error}</Alert>}
          <div>
            <Text size="sm" fw={500}>Installed on</Text>
            {(s.installations ?? []).length === 0
              ? <Text size="sm" c="dimmed">nothing yet</Text>
              : <Group gap={6} mt={4}>{s.installations!.map(i => <Badge key={i.id} variant="light">{i.account}</Badge>)}</Group>}
            <Anchor size="sm" href={s.installUrl} target="_blank">Choose repositories <IconExternalLink size={11} /></Anchor>
          </div>
          <Group gap="xs">
            <Text size="sm">Push webhook</Text>
            <Badge color={s.webhookActive ? "green" : "gray"} variant="light">{s.webhookActive ? "on" : "off"}</Badge>
            <Code>{s.webhookUrl}</Code>
          </Group>
          <NumberInput label="Check for new commits every (minutes)" value={poll} onChange={v => setPoll(v === "" ? "" : Number(v))}
            min={0} max={1440} maw={260}
            description="0 turns checking off. With the webhook on, a check only catches pushes it missed." />
          <Switch checked={login} onChange={e => setLogin(e.currentTarget.checked)}
            label="Offer “Sign in with GitHub” on the login page"
            description="Only for GitHub accounts someone linked on their profile here." />
          <Group>
            <Button onClick={save} loading={busy}>Save</Button>
            <Button variant="subtle" color="red" onClick={forget}>Forget app</Button>
          </Group>
        </>
      )}
    </MStack>
  );
}
