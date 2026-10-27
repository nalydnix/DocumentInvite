import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { Pagination } from './Pagination';

function setup(props: Partial<Parameters<typeof Pagination>[0]> = {}) {
  const onPageChange = vi.fn();
  const onPageSizeChange = vi.fn();
  render(
    <Pagination
      page={2}
      pageSize={25}
      total={60}
      onPageChange={onPageChange}
      onPageSizeChange={onPageSizeChange}
      {...props}
    />,
  );
  return { onPageChange, onPageSizeChange };
}

describe('Pagination', () => {
  it('shows the row range, the page indicator and the total', () => {
    setup();

    expect(screen.getByText('Showing 26–50 of 60')).toBeInTheDocument();
    expect(screen.getByText('Page 2 of 3')).toBeInTheDocument();
  });

  it('shortens the range on the last page', () => {
    setup({ page: 3 });

    expect(screen.getByText('Showing 51–60 of 60')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Next' })).toBeDisabled();
  });

  it('disables Previous on the first page', () => {
    setup({ page: 1 });

    expect(screen.getByRole('button', { name: 'Previous' })).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Next' })).toBeEnabled();
  });

  it('asks for the previous and next page', async () => {
    const { onPageChange } = setup();
    const user = userEvent.setup();

    await user.click(screen.getByRole('button', { name: 'Next' }));
    await user.click(screen.getByRole('button', { name: 'Previous' }));

    expect(onPageChange).toHaveBeenNthCalledWith(1, 3);
    expect(onPageChange).toHaveBeenNthCalledWith(2, 1);
  });

  it('reports a new page size', async () => {
    const { onPageSizeChange } = setup();

    await userEvent.setup().selectOptions(screen.getByLabelText('Rows per page'), '50');

    expect(onPageSizeChange).toHaveBeenCalledWith(50);
  });

  it('shows a single page with both buttons disabled', () => {
    setup({ page: 1, total: 3 });

    expect(screen.getByText('Showing 1–3 of 3')).toBeInTheDocument();
    expect(screen.getByText('Page 1 of 1')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Previous' })).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Next' })).toBeDisabled();
  });

  it('shows the last page when the page is past the end', () => {
    setup({ page: 9 });

    expect(screen.getByText('Page 3 of 3')).toBeInTheDocument();
  });

  it('renders nothing when there are no rows', () => {
    setup({ total: 0, page: 1 });

    expect(screen.queryByRole('navigation', { name: 'Pagination' })).not.toBeInTheDocument();
  });
});
