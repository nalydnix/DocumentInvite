import { render, screen } from '@testing-library/react';
import { MemoryRouter, Route, Routes, useLocation } from 'react-router-dom';
import { describe, expect, it } from 'vitest';
import { LegacyRedirect } from './App';

function Where() {
  const { pathname, search } = useLocation();
  return <p>{`at ${pathname}${search}`}</p>;
}

function renderAt(url: string) {
  render(
    <MemoryRouter initialEntries={[url]}>
      <Routes>
        <Route path="/loans" element={<LegacyRedirect to="/requests" />} />
        <Route path="/loans/:id" element={<LegacyRedirect to="/requests" />} />
        <Route path="*" element={<Where />} />
      </Routes>
    </MemoryRouter>,
  );
}

describe('old staff URLs', () => {
  it('redirects the list to /requests and keeps the query string', () => {
    renderAt('/loans?status=Sent');
    expect(screen.getByText('at /requests?status=Sent')).toBeInTheDocument();
  });

  it('redirects a detail page to /requests/:id', () => {
    renderAt('/loans/abc-123');
    expect(screen.getByText('at /requests/abc-123')).toBeInTheDocument();
  });
});
