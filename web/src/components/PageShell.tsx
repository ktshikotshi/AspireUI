import { useEffect, useReducer, type ReactNode } from "react";
import { useNavigate } from "react-router-dom";
import { AppShell, Container, Group, Title, Anchor, Text, Tooltip, ActionIcon, SegmentedControl } from "@mantine/core";
import { IconBrandGithub, IconHelp, IconArrowLeft, IconLayoutGrid, IconLayoutDashboard } from "@tabler/icons-react";
import { APP_VERSION, BUILD_INFO } from "../model";
import { UserMenu } from "../auth/UserMenu";
import { DOCS_URL } from "../HelpButton";
import { useViewMode, type ViewMode } from "../viewMode";
import logo from "../assets/logo.svg";

// Page shell providing consistent header/footer chrome with optional back button and title.
export function PageShell({ title, back = true, actions, children, container = "xl" }: {
  title?: ReactNode; back?: boolean; actions?: ReactNode; children: ReactNode;
  container?: "xl" | "lg" | "md" | "sm" | false;
}) {
  const nav = useNavigate();
  const { mode, canToggle, setMode } = useViewMode();
  const [, bump] = useReducer(x => x + 1, 0);
  useEffect(() => { const h = () => bump(); window.addEventListener("aspireui:mode-changed", h); return () => window.removeEventListener("aspireui:mode-changed", h); }, []);
  const switchMode = (m: ViewMode) => { setMode(m); nav("/"); };
  return (
    <AppShell header={{ height: 56 }} padding="lg">
      <AppShell.Header withBorder>
        <Container size="xl" h="100%">
          <Group h="100%" justify="space-between" wrap="nowrap">
            <Group gap="sm" wrap="nowrap" style={{ flex: 1, minWidth: 0 }}>
              <Anchor onClick={() => nav("/")} style={{ display: "flex", alignItems: "center", gap: 8, cursor: "pointer" }} underline="never" c="inherit">
                <img src={logo} alt="" height={26} style={{ display: "block" }} />
                <Text fw={700} size="sm">AspireUI</Text>
              </Anchor>
              {title && <>
                <Text c="dimmed" size="lg" fw={300}>/</Text>
                {back && <Tooltip label="Back to apps" withArrow>
                  <ActionIcon variant="subtle" color="gray" size="sm" onClick={() => nav("/")} aria-label="Back"><IconArrowLeft size={16} /></ActionIcon>
                </Tooltip>}
                <Title order={5} fw={600}>{title}</Title>
              </>}
            </Group>
            {canToggle && (
              <SegmentedControl size="xs" radius="md" value={mode} onChange={v => switchMode(v as ViewMode)}
                data={[
                  { value: "simple", label: <Group gap={6} wrap="nowrap"><IconLayoutGrid size={15} /> My apps</Group> },
                  { value: "full", label: <Group gap={6} wrap="nowrap"><IconLayoutDashboard size={15} /> Builder</Group> },
                ]} />
            )}
            <Group gap="sm" wrap="nowrap" style={{ flex: 1, minWidth: 0, justifyContent: "flex-end" }}>
              {actions}
              <UserMenu />
            </Group>
          </Group>
        </Container>
      </AppShell.Header>

      <AppShell.Main>
        {container ? <Container size={container}>{children}</Container> : children}
        <Container size="xl" mt={48}>
          <Group justify="center" gap={8} opacity={0.7}>
            <img src={logo} alt="" height={18} style={{ display: "block" }} />
            <Tooltip label={`build ${BUILD_INFO}`} withArrow>
              <Anchor size="xs" c="dimmed" href="https://fgilde.github.io/AspireUI" target="_blank" rel="noreferrer">AspireUI v{APP_VERSION}</Anchor>
            </Tooltip>
            <Text size="xs" c="dimmed">·</Text>
            <Tooltip label="GitHub" withArrow>
              <ActionIcon component="a" href="https://github.com/ktshikotshi/AspireUI" target="_blank" rel="noreferrer" variant="subtle" color="gray" size="sm" aria-label="GitHub">
                <IconBrandGithub size={15} />
              </ActionIcon>
            </Tooltip>
            <Tooltip label="Documentation" withArrow>
              <ActionIcon component="a" href={DOCS_URL} target="_blank" rel="noreferrer" variant="subtle" color="gray" size="sm" aria-label="Documentation">
                <IconHelp size={15} />
              </ActionIcon>
            </Tooltip>
            <Text size="xs" c="dimmed">·</Text>
            <Anchor size="xs" c="dimmed" href="https://www.gilde.org" target="_blank" rel="noreferrer">by gilde.org</Anchor>
          </Group>
        </Container>
      </AppShell.Main>
    </AppShell>
  );
}
