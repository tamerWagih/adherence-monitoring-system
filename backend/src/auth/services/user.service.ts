import { Injectable } from '@nestjs/common';
import { InjectRepository } from '@nestjs/typeorm';
import { Repository } from 'typeorm';
import { User } from '../../entities/user.entity';
import { UserRole } from '../../entities/user-role.entity';

@Injectable()
export class UserService {
  constructor(
    @InjectRepository(User)
    private userRepo: Repository<User>,
    @InjectRepository(UserRole)
    private userRoleRepo: Repository<UserRole>,
  ) {}

  /**
   * Find user by email
   */
  async findByEmail(email: string): Promise<User | null> {
    const user = await this.userRepo.findOne({
      where: { email },
      relations: ['userRoles', 'userRoles.role'],
    });

    return user || null;
  }

  /**
   * Find user by ID
   */
  async findById(id: string): Promise<User | null> {
    const user = await this.userRepo.findOne({
      where: { id },
      relations: ['userRoles', 'userRoles.role'],
    });

    return user || null;
  }

  /**
   * Get user permissions from roles
   */
  async getUserPermissions(userId: string): Promise<string[]> {
    // Query permissions through user roles
    const result = await this.userRoleRepo
      .createQueryBuilder('userRole')
      .innerJoin('userRole.role', 'role')
      .innerJoin('role_permissions', 'rp', 'rp."roleId" = role.id')
      .innerJoin('permissions', 'p', 'p.id = rp."permissionId"')
      .where('userRole.userId = :userId', { userId })
      .select('p.name', 'permission')
      .distinct(true)
      .getRawMany();

    return result.map((r) => r.permission);
  }

  /**
   * Update last login timestamp
   */
  async updateLastLogin(userId: string): Promise<void> {
    await this.userRepo.update(userId, {
      lastLoginAt: new Date(),
    });
  }
}

