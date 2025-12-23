import {
  Entity,
  PrimaryGeneratedColumn,
  Column,
  OneToMany,
} from 'typeorm';
import * as bcrypt from 'bcrypt';
import { UserRole } from './user-role.entity';

/**
 * User Entity
 * 
 * Reads from shared users table in database.
 * Includes password and roles for authentication.
 */
@Entity('users')
export class User {
  @PrimaryGeneratedColumn('uuid')
  id: string;

  @Column({ unique: true })
  email: string;

  @Column()
  password: string;

  @Column()
  firstName: string;

  @Column()
  lastName: string;

  @Column({ default: true })
  isActive: boolean;

  @Column({ name: 'lastLoginAt', type: 'timestamptz', nullable: true })
  lastLoginAt?: Date | null;

  @Column({ name: 'employee_id', nullable: true })
  employeeId?: string;

  @OneToMany(() => UserRole, (userRole) => userRole.user)
  userRoles: UserRole[];

  // Virtual property to get roles
  get roles(): string[] {
    return this.userRoles?.map((userRole) => userRole.role?.name) || [];
  }

  // Method to compare password
  async comparePassword(candidatePassword: string): Promise<boolean> {
    return bcrypt.compare(candidatePassword, this.password);
  }
}

