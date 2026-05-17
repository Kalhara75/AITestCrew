import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import userEvent from '@testing-library/user-event';

vi.mock('../api/xray', () => ({
  previewXrayImport: vi.fn(),
  confirmXrayImport: vi.fn(),
}));

import { previewXrayImport, confirmXrayImport } from '../api/xray';
import { ImportFromXrayDialog } from './ImportFromXrayDialog';
import type { XrayImportPreview, XrayImportResult } from '../api/xray';

function makePreview(): XrayImportPreview {
  return {
    ticketKey: 'PROJ-1234',
    ticketSummary: 'Test ticket',
    moduleId: 'mod',
    testSetId: 'ts1',
    proposedObjectives: [
      {
        slug: 'obj-a',
        title: 'Objective A',
        rationale: 'Rationale A',
        assignedFragments: [],
        mappingRows: [{ sourceFragment: 'step 1', kind: 'API', confidence: 0.9, rationale: 'r' }],
        preconditions: [],
      },
      {
        slug: 'obj-b',
        title: 'Objective B',
        rationale: 'Rationale B',
        assignedFragments: [],
        mappingRows: [{ sourceFragment: 'step 2', kind: 'API', confidence: 0.9, rationale: 'r' }],
        preconditions: [],
      },
      {
        slug: 'obj-c',
        title: 'Objective C',
        rationale: 'Rationale C',
        assignedFragments: [],
        mappingRows: [{ sourceFragment: 'step 3', kind: 'API', confidence: 0.9, rationale: 'r' }],
        preconditions: [],
      },
    ],
    reviewCarefullyFlag: false,
    draftGapReqTitles: [],
  };
}

function makeResult(): XrayImportResult {
  return {
    persistedObjectiveIds: ['id-1', 'id-2'],
    gapReqPaths: [],
    placeholderStepDescriptions: [],
  };
}

/**
 * Render the dialog in 'review' phase by mocking previewXrayImport to resolve immediately.
 */
async function renderInReview(onImported = vi.fn()) {
  const preview = makePreview();
  (previewXrayImport as ReturnType<typeof vi.fn>).mockResolvedValue(preview);
  (confirmXrayImport as ReturnType<typeof vi.fn>).mockResolvedValue(makeResult());

  const utils = render(
    <ImportFromXrayDialog
      open={true}
      moduleId='mod'
      testSetId='ts1'
      onClose={vi.fn()}
      onImported={onImported}
    />
  );

  // Type a ticket key and click Preview to trigger previewXrayImport
  const input = screen.getByPlaceholderText('e.g. PROJ-1234');
  await userEvent.type(input, 'PROJ-1234');
  await userEvent.click(screen.getByRole('button', { name: 'Preview Import' }));

  // Wait for review phase to render
  // Wait for review phase -- title is in an input value, not text content
  await screen.findByDisplayValue('Objective A');

  return { ...utils, preview };
}

describe('ImportFromXrayDialog', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  // AC#8(a): uncheck a card -> confirm -> acceptedObjectiveSlugs omits that slug
  it('uncheck a card then confirm omits its slug from acceptedObjectiveSlugs', async () => {
    await renderInReview();

    // Uncheck obj-a (first checkbox)
    const checkboxes = screen.getAllByRole('checkbox');
    // checkboxes[0] = Collapse all, checkboxes[1..3] = obj-a, obj-b, obj-c
    // Find the obj-a checkbox specifically by its card position
    // The collapse-to-single checkbox is the last one; the obj checkboxes come first
    const objCheckboxes = checkboxes.filter(cb => cb !== checkboxes[checkboxes.length - 1]);
    await userEvent.click(objCheckboxes[0]);  // uncheck obj-a

    await userEvent.click(screen.getByRole('button', { name: /Import/i }));

    expect(confirmXrayImport).toHaveBeenCalledTimes(1);
    const req = (confirmXrayImport as ReturnType<typeof vi.fn>).mock.calls[0][0];
    expect(req.acceptedObjectiveSlugs).not.toContain('obj-a');
    expect(req.acceptedObjectiveSlugs).toContain('obj-b');
    expect(req.acceptedObjectiveSlugs).toContain('obj-c');
  });

  // AC#8(b): edit a title -> confirm -> titleOverrides has the edited value
  it('editing a title then confirm sends titleOverrides', async () => {
    await renderInReview();

    // Find the title input for obj-a (aria-label = 'Title for objective 1')
    const titleInput = screen.getByRole('textbox', { name: 'Title for objective 1' });
    await userEvent.clear(titleInput);
    await userEvent.type(titleInput, 'Updated Title A');
    fireEvent.blur(titleInput);

    await userEvent.click(screen.getByRole('button', { name: /Import/i }));

    expect(confirmXrayImport).toHaveBeenCalledTimes(1);
    const req = (confirmXrayImport as ReturnType<typeof vi.fn>).mock.calls[0][0];
    expect(req.titleOverrides['obj-a']).toBe('Updated Title A');
  });

  // AC#8(c): merge obj-b into obj-a -> confirm -> mergeRequests has the entry, obj-b not in acceptedSlugs
  it('merge a card then confirm sends mergeRequests and excludes slugToMerge', async () => {
    await renderInReview();

    // Click 'Merge into above' on the second card (obj-b)
    const mergeButtons = screen.getAllByRole('button', { name: /Merge into above/i });
    await userEvent.click(mergeButtons[0]);  // first merge button = obj-b

    await userEvent.click(screen.getByRole('button', { name: /Import/i }));

    expect(confirmXrayImport).toHaveBeenCalledTimes(1);
    const req = (confirmXrayImport as ReturnType<typeof vi.fn>).mock.calls[0][0];
    expect(req.mergeRequests).toHaveLength(1);
    expect(req.mergeRequests[0].slugToMerge).toBe('obj-b');
    expect(req.mergeRequests[0].mergeIntoSlug).toBe('obj-a');
    expect(req.acceptedObjectiveSlugs).not.toContain('obj-b');
  });

  // AC#8(d): collapse-on greys controls; collapse-off restores per-card state
  it('collapse toggle disables per-card controls but preserves state when toggled off', async () => {
    await renderInReview();

    // Uncheck obj-a before collapsing
    const checkboxes = screen.getAllByRole('checkbox');
    const objCheckboxes = checkboxes.filter(cb => cb !== checkboxes[checkboxes.length - 1]);
    await userEvent.click(objCheckboxes[0]);  // uncheck obj-a

    // Turn on collapse
    const collapseToggle = checkboxes[checkboxes.length - 1];
    await userEvent.click(collapseToggle);

    // Per-card checkboxes should be disabled
    const checkboxesAfterCollapse = screen.getAllByRole('checkbox');
    const objCheckboxesAfter = checkboxesAfterCollapse.filter(cb => cb !== checkboxesAfterCollapse[checkboxesAfterCollapse.length - 1]);
    for (const cb of objCheckboxesAfter) {
      expect(cb).toBeDisabled();
    }

    // Turn off collapse
    await userEvent.click(collapseToggle);

    // Re-read checkboxes -- obj-a should still be unchecked (state preserved)
    const checkboxesRestored = screen.getAllByRole('checkbox');
    const objCheckboxesRestored = checkboxesRestored.filter(cb => cb !== checkboxesRestored[checkboxesRestored.length - 1]);
    expect(objCheckboxesRestored[0]).not.toBeChecked();  // obj-a still unchecked
    expect(objCheckboxesRestored[1]).toBeChecked();     // obj-b still checked
  });

  // AC#7: no-touch confirm sends [] for acceptedObjectiveSlugs (existing behaviour preserved)
  it('untouched confirm sends empty acceptedObjectiveSlugs preserving accept-all wire shape', async () => {
    await renderInReview();

    await userEvent.click(screen.getByRole('button', { name: /Import/i }));

    const req = (confirmXrayImport as ReturnType<typeof vi.fn>).mock.calls[0][0];
    expect(req.acceptedObjectiveSlugs).toEqual([]);
    expect(req.mergeRequests).toEqual([]);
    expect(req.titleOverrides).toEqual({});
  });

});
