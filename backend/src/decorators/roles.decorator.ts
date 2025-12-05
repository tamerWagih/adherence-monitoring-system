import { SetMetadata } from '@nestjs/common';

export const ROLES_KEY = 'roles';

/**
 * Roles Decorator
 * 
 * Used to specify required roles for a route.
 * 
 * @example
 * @Roles('WFM_Admin')
 * @Get('admin/workstations')
 */
export const Roles = (...roles: string[]) => SetMetadata(ROLES_KEY, roles);

