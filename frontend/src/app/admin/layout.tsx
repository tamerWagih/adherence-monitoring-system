import React from 'react';
import Link from 'next/link';

export default function AdminLayout({
  children,
}: {
  children: React.ReactNode;
}) {
  return (
    <div className="min-h-screen bg-gray-50">
      {/* Sidebar Navigation */}
      <aside className="fixed left-0 top-0 h-full w-64 bg-white border-r border-gray-200">
        <div className="p-6">
          <h1 className="text-2xl font-bold text-gray-900">
            Adherence Admin
          </h1>
        </div>
        <nav className="mt-6">
          <ul className="space-y-1 px-3">
            <li>
              <Link
                href="/admin/dashboard"
                className="block px-4 py-2 text-gray-700 hover:bg-gray-100 rounded-lg"
              >
                Dashboard
              </Link>
            </li>
            <li>
              <Link
                href="/admin/devices"
                className="block px-4 py-2 text-gray-700 hover:bg-gray-100 rounded-lg"
              >
                Devices
              </Link>
            </li>
            <li>
              <Link
                href="/admin/agents"
                className="block px-4 py-2 text-gray-700 hover:bg-gray-100 rounded-lg"
              >
                Agent Sync Status
              </Link>
            </li>
            <li>
              <Link
                href="/admin/config"
                className="block px-4 py-2 text-gray-700 hover:bg-gray-100 rounded-lg"
              >
                Configuration
              </Link>
            </li>
            <li>
              <Link
                href="/admin/health"
                className="block px-4 py-2 text-gray-700 hover:bg-gray-100 rounded-lg"
              >
                System Health
              </Link>
            </li>
          </ul>
        </nav>
      </aside>

      {/* Main Content */}
      <main className="ml-64 p-8">
        {children}
      </main>
    </div>
  );
}

