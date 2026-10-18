import { useMemo } from "react";
import { ScrollArea, Table, Badge, Text, Code, Group, ThemeIcon } from "@mantine/core";
import { IconCircleCheck, IconAlertTriangle } from "@tabler/icons-react";
import { useEditor, usePanelFlash, PANEL_FLASH_STYLE } from "./DockLayout";
import { lintStack } from "../model";

// Live validation panel showing Roslyn diagnostics and graph linting results.
export function ValidationPanel() {
  const { diagnostics: roslyn, stack } = useEditor();
  const flash = usePanelFlash("validation");

  // An imported AppHost's nodes are a best-effort picture of code that runs as written; linting them
  // reports problems the code does not have.
  const lint = useMemo(() => stack.runAsIs ? [] : lintStack(stack).map(l => ({ ...l, message: `[graph] ${l.message}`, start: 0, end: 0 })), [stack]);
  const diagnostics = useMemo(() => [...lint, ...roslyn], [lint, roslyn]);

  const errors = diagnostics.filter(d => d.severity === "error");
  const warns = diagnostics.filter(d => d.severity === "warning");

  return (
    <ScrollArea style={{ height: "100%", ...(flash ? PANEL_FLASH_STYLE : {}) }} px="sm" py="xs">
      {diagnostics.length === 0 ? (
        <Group gap={8}><ThemeIcon color="green" variant="light" size="sm"><IconCircleCheck size={14} /></ThemeIcon>
          <Text size="sm" c="green">{stack.runAsIs
            ? "No syntax errors. This imported AppHost runs as written, so it is fully compiled when you run or deploy it."
            : "No issues — the generated code compiles cleanly."}</Text></Group>
      ) : (
        <>
          <Group gap={8} mb="xs">
            <ThemeIcon color={errors.length ? "red" : "yellow"} variant="light" size="sm"><IconAlertTriangle size={14} /></ThemeIcon>
            <Text size="sm" fw={600}>{errors.length} error(s), {warns.length} warning(s)</Text>
          </Group>
          <Table verticalSpacing={4} striped highlightOnHover>
            <Table.Tbody>
              {diagnostics.map((d, i) => (
                <Table.Tr key={i}>
                  <Table.Td width={70}><Badge size="xs" color={d.severity === "error" ? "red" : "yellow"}>{d.severity}</Badge></Table.Td>
                  <Table.Td><Code style={{ whiteSpace: "pre-wrap", fontSize: 11, background: "transparent" }}>{d.message}</Code></Table.Td>
                </Table.Tr>
              ))}
            </Table.Tbody>
          </Table>
        </>
      )}
    </ScrollArea>
  );
}
