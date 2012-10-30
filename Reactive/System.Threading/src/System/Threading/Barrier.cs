﻿// ==++==
//
//   Copyright (c) Microsoft Corporation.  All rights reserved.
// 
// ==--==
// =+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+
//
// Barrier.cs
//
// <OWNER>emadali</OWNER>
//
// A barrier allows multiple tasks to cooperatively work on some algorithm in parallel.
// A group of tasks cooperate by moving through a series of phases, where each in the group signals it has arrived at
// the barrier in a given phase and implicitly waits for all others to arrive. 
// The same barrier can be used for multiple phases.
//
// =-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Security.Permissions;
using System.Runtime.InteropServices;
using System.Threading;
using System.Runtime.Serialization;
using System.Security;
namespace System.Threading
{
    /// <summary>
    /// The exception that is thrown when the post-phase action of a <see cref="Barrier"/> fails.
    /// </summary>
    [Serializable]
    public class BarrierPostPhaseException : Exception
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="BarrierPostPhaseException"/> class.
        /// </summary>
        public BarrierPostPhaseException():this((string)null)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="BarrierPostPhaseException"/> class with the specified inner exception.
        /// </summary>
        /// <param name="innerException">The exception that is the cause of the current exception.</param>
        public BarrierPostPhaseException(Exception innerException): this(null, innerException)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="BarrierPostPhaseException"/> class with a specified error message.
        /// </summary>
        /// <param name="message">A string that describes the exception.</param>
        public BarrierPostPhaseException(string message):this(message, null)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="BarrierPostPhaseException"/> class with a specified error message and inner exception.
        /// </summary>
        /// <param name="message">A string that describes the exception.</param>
        /// <param name="innerException">The exception that is the cause of the current exception.</param>
        public BarrierPostPhaseException(string message, Exception innerException)
            : base(message == null ? "BarrierPostPhaseException" : message, innerException)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="BarrierPostPhaseException"/> class with serialized data.
        /// </summary>
        /// <param name="info">The object that holds the serialized object data.</param>
        /// <param name="context">An object that describes the source or destination of the serialized data.</param>
        [SecurityCritical]
        protected BarrierPostPhaseException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        {
        }
    }


    /// <summary>
    /// Enables multiple tasks to cooperatively work on an algorithm in parallel through multiple phases.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A group of tasks cooperate by moving through a series of phases, where each in the group signals it
    /// has arrived at the <see cref="Barrier"/> in a given phase and implicitly waits for all others to
    /// arrive. The same <see cref="Barrier"/> can be used for multiple phases.
    /// </para>
    /// <para>
    /// All public and protected members of <see cref="Barrier"/> are thread-safe and may be used
    /// concurrently from multiple threads, with the exception of Dispose, which
    /// must only be used when all other operations on the <see cref="Barrier"/> have
    /// completed.
    /// </para>
    /// </remarks>
    [ComVisible(false)]
    [HostProtection(SecurityAction.LinkDemand, Synchronization = true, ExternalThreading = true)]
    [DebuggerDisplay("Participant Count={ParticipantCount},Participants Remaining={ParticipantsRemaining}")]
    public class Barrier : IDisposable
    {

        //This variable holds the basic barrier variables: 
        // 1- The current particiants count 
        // 2- The total participants count
        // 3- The sense flag (true if the cuurrent phase is even, false otherwise)
        // The first 15 bits are for the total count which means the maximum participants for the barrier is about 32K
        // The 16th bit is dummy
        // The next 15th bit for the current
        // And the last highest bit is for the sense
        volatile int m_currentTotalCount;

        // Bitmask to extract the current count
        const int CURRENT_MASK = 0x7FFF0000;

        // Bitmask to extract the total count
        const int TOTAL_MASK = 0x00007FFF;

        // Bitmask to extratc the sense flag
        const int SENSE_MASK = unchecked((int)0x80000000);

        // The maximum participants the barrier can operate = 32767 ( 2 power 15 - 1 )
        const int MAX_PARTICIPANTS = TOTAL_MASK;


        // The current barrier phase
        // We don't need to worry about overflow, the max value is 2^63-1; If it starts from 0 at a
        // rate of 4 billion increments per second, it will takes about 64 years to overflow.
        long m_currentPhase;


        // dispose flag
        bool m_disposed;

        // Odd phases event
        ManualResetEventSlim m_oddEvent;

        // Even phases event
        ManualResetEventSlim m_evenEvent;

        // The execution context of the creator thread
        ExecutionContext m_ownerThreadContext;

        // Post phase action after each phase
        Action<Barrier> m_postPhaseAction;

        // In case the post phase action throws an exception, wraps it in BarrierPostPhaseException
        Exception m_exception;

        // This is the ManagedThreadID of the postPhaseAction caller thread, this is used to determine if the SignalAndWait, Dispose or Add/RemoveParticipant caller thread is
        // the same thread as the postPhaseAction thread which means this method was called from the postPhaseAction which is illegal.
        // This value is captured before calling the action and reset back to zero after it.
        int m_actionCallerID;

        #region Properties

        /// <summary>
        /// Gets the number of participants in the barrier that haven’t yet signaled
        /// in the current phase.
        /// </summary>
        /// <remarks>
        /// This could be 0 during a post-phase action delegate execution or if the
        /// ParticipantCount is 0.
        /// </remarks>
        public int ParticipantsRemaining
        {
            get
            {
                int currentTotal = m_currentTotalCount;
                int total = (int)(currentTotal & TOTAL_MASK);
                int current = (int)((currentTotal & CURRENT_MASK) >> 16);
                return total - current;
            }
        }

        /// <summary>
        /// Gets the total number of participants in the barrier.
        /// </summary>
        public int ParticipantCount
        {
            get { return (int)(m_currentTotalCount & TOTAL_MASK); }
        }

        /// <summary>
        /// Gets the number of the barrier's current phase.
        /// </summary>
        public long CurrentPhaseNumber
        {
            get { return m_currentPhase; }
        }

        #endregion

        /// <summary>
        /// Initializes a new instance of the <see cref="Barrier"/> class.
        /// </summary>
        /// <param name="participantCount">The number of participating threads.</param>
        /// <exception cref="ArgumentOutOfRangeException"> <paramref name="participantCount"/> is less than 0
        /// or greater than <see cref="T:System.Int32.MaxValue"/>.</exception>
        public Barrier(int participantCount)
            : this(participantCount, null)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="Barrier"/> class.
        /// </summary>
        /// <param name="participantCount">The number of participating threads.</param>
        /// <param name="postPhaseAction">The <see cref="T:System.Action`1"/> to be executed after each
        /// phase.</param>
        /// <exception cref="T:System.ArgumentOutOfRangeException"> <paramref name="participantCount"/> is less than 0
        /// or greater than <see cref="T:System.Int32.MaxValue"/>.</exception>
        /// <remarks>
        /// The <paramref name="postPhaseAction"/> delegate will be executed after
        /// all participants have arrived at the barrier in one phase.  The participants
        /// will not be released to the next phase until the postPhaseAction delegate
        /// has completed execution.
        /// </remarks>
        public Barrier(int participantCount, Action<Barrier> postPhaseAction)
        {
            // the count must be non negative value
            if (participantCount < 0 || participantCount > MAX_PARTICIPANTS)
            {
                throw new ArgumentOutOfRangeException("participantCount", participantCount, "Barrier_ctor_ArgumentOutOfRange");
            }
            m_currentTotalCount = (int)participantCount;
            m_postPhaseAction = postPhaseAction;

            //Lazily initialize the events
            m_oddEvent = new ManualResetEventSlim(true);
            m_evenEvent = new ManualResetEventSlim(false);

            // Capture the context if the post phase action is not null
            if (postPhaseAction != null && !ExecutionContext.IsFlowSuppressed())
            {
                m_ownerThreadContext = ExecutionContext.Capture();
            }

            m_actionCallerID = 0;

        }

        /// <summary>
        /// Extract the three variables current, total and sense from a given big variable
        /// </summary>
        /// <param name="currentTotal">The integer variable that contains the other three variables</param>
        /// <param name="current">The current cparticipant count</param>
        /// <param name="total">The total participants count</param>
        /// <param name="sense">The sense flag</param>
        private void GetCurrentTotal(int currentTotal, out int current, out int total, out bool sense)
        {
            total = (int)(currentTotal & TOTAL_MASK);
            current = (int)((currentTotal & CURRENT_MASK) >> 16);
            sense = (currentTotal & SENSE_MASK) == 0 ? true : false;
        }

        /// <summary>
        /// Write the three variables current. total and the sense to the m_currentTotal
        /// </summary>
        /// <param name="currentTotal">The old current total to compare</param>
        /// <param name="current">The current cparticipant count</param>
        /// <param name="total">The total participants count</param>
        /// <param name="sense">The sense flag</param>
        /// <returns>True if the CAS succeeded, false otherwise</returns>
        private bool SetCurrentTotal(int currentTotal, int current, int total, bool sense)
        {
            int newCurrentTotal = (current <<16) | total;
            
            if (!sense)
            {
                newCurrentTotal |= SENSE_MASK;
            }

#pragma warning disable 0420
            return Interlocked.CompareExchange(ref m_currentTotalCount, newCurrentTotal, currentTotal) == currentTotal;
#pragma warning restore 0420
        }

        /// <summary>
        /// Notifies the <see cref="Barrier"/> that there will be an additional participant.
        /// </summary>
        /// <returns>The phase number of the barrier in which the new participants will first
        /// participate.</returns>
        /// <exception cref="T:System.InvalidOperationException">
        /// Adding a participant would cause the barrier's participant count to 
        /// exceed <see cref="T:System.Int16.MaxValue"/>.
        /// </exception>
        /// <exception cref="T:System.InvalidOperationException">
        /// The method was invoked from within a post-phase action.
        /// </exception>
        /// <exception cref="T:System.ObjectDisposedException">The current instance has already been
        /// disposed.</exception>
        public long AddParticipant()
        {
            try
            {
                return AddParticipants(1);
            }
            catch (ArgumentOutOfRangeException)
            {
                throw new InvalidOperationException("Barrier_AddParticipants_Overflow_ArgumentOutOfRange");
            }
        }

        /// <summary>
        /// Notifies the <see cref="Barrier"/> that there will be additional participants.
        /// </summary>
        /// <param name="participantCount">The number of additional participants to add to the
        /// barrier.</param>
        /// <returns>The phase number of the barrier in which the new participants will first
        /// participate.</returns>
        /// <exception cref="T:System.ArgumentOutOfRangeException"><paramref name="participantCount"/> is less than
        /// 0.</exception>
        /// <exception cref="T:System.ArgumentOutOfRangeException">Adding <paramref name="participantCount"/> participants would cause the
        /// barrier's participant count to exceed <see cref="T:System.Int16.MaxValue"/>.</exception>
        /// <exception cref="T:System.InvalidOperationException">
        /// The method was invoked from within a post-phase action.
        /// </exception>
        /// <exception cref="T:System.ObjectDisposedException">The current instance has already been
        /// disposed.</exception>
        public long AddParticipants(int participantCount)
        {
            // check dispose
            ThrowIfDisposed();

            if (participantCount < 1 )
            {
                throw new ArgumentOutOfRangeException("participantCount", participantCount,
                    "Barrier_AddParticipants_NonPositive_ArgumentOutOfRange");
            }
            else if (participantCount > MAX_PARTICIPANTS) //overflow
            {
                throw new ArgumentOutOfRangeException("participantCount",
                        "Barrier_AddParticipants_Overflow_ArgumentOutOfRange");
            }

            // in case of this is called from the PHA
            if (m_actionCallerID != 0 && Thread.CurrentThread.ManagedThreadId == m_actionCallerID)
            {
                throw new InvalidOperationException("Barrier_InvalidOperation_CalledFromPHA");
            }

            SpinWait spinner = new SpinWait();
            long newPhase = 0;
            while (true)
            {
                int currentTotal = m_currentTotalCount;
                int total;
                int current;
                bool sense;
                GetCurrentTotal(currentTotal, out current, out total, out sense);
                if (participantCount + total > MAX_PARTICIPANTS) //overflow
                {
                    throw new ArgumentOutOfRangeException("participantCount",
                        "Barrier_AddParticipants_Overflow_ArgumentOutOfRange");
                }

                if (SetCurrentTotal(currentTotal, current, total + participantCount, sense))
                {
                    // Calculating the first phase for that participant, if the current phase already finished return the nextphase else return the current phase
                    // To know that the current phase is  the sense doesn't match the 
                    // phase odd even, so that means it didn't yet change the phase count, so currentPhase +1 is returned, otherwise currentPhase is returned
                    long currPhase = m_currentPhase;
                    newPhase = (sense != (currPhase % 2 == 0)) ? currPhase + 1 : currPhase;

                    // If this participants goona join the next phase, which means the postPhaseAction is being running, this participants must wait until this done
                    // and its event is reset.
                    // Without that, if the postPhaseAction takes long time, this means the event ehich the current participant is goint to wait on is still set 
                    // (FinishPPhase didn't reset it yet) so it should wait until it reset
                    if (newPhase != currPhase)
                    {
                        // Wait on the opposite event
                        if (sense)
                        {
                            m_oddEvent.Wait();
                        }
                        else
                        {
                            m_evenEvent.Wait();
                        }
                    }

                    //This else to fix the racing where the current phase has been finished, m_currentPhase has been updated but the events have not been set/reset yet
                    // otherwise when this participant calls SignalAndWait it will wait on a set event however all other participants have not arrived yet.
                    else
                    {
                        if (sense && m_evenEvent.IsSet)
                            m_evenEvent.Reset();
                        else if (!sense && m_oddEvent.IsSet)
                            m_oddEvent.Reset();
                    }
                    break;
                }
                spinner.SpinOnce();
            }
            return newPhase;
        }

        /// <summary>
        /// Notifies the <see cref="Barrier"/> that there will be one less participant.
        /// </summary>
        /// <exception cref="T:System.InvalidOperationException">The barrier already has 0
        /// participants.</exception>
        /// <exception cref="T:System.InvalidOperationException">
        /// The method was invoked from within a post-phase action.
        /// </exception>
        /// <exception cref="T:System.ObjectDisposedException">The current instance has already been
        /// disposed.</exception>
        public void RemoveParticipant()
        {
            RemoveParticipants(1);
        }

        /// <summary>
        /// Notifies the <see cref="Barrier"/> that there will be fewer participants.
        /// </summary>
        /// <param name="participantCount">The number of additional participants to remove from the barrier.</param>
        /// <exception cref="T:System.ArgumentOutOfRangeException"><paramref name="participantCount"/> is less than
        /// 0.</exception>
        /// <exception cref="T:System.InvalidOperationException">The barrier already has 0 participants.</exception>
        /// <exception cref="T:System.InvalidOperationException">
        /// The method was invoked from within a post-phase action.
        /// </exception>
        /// <exception cref="T:System.ObjectDisposedException">The current instance has already been
        /// disposed.</exception>
        public void RemoveParticipants(int participantCount)
        {
            // check dispose 
            ThrowIfDisposed();

            // Validate input
            if (participantCount < 1)
            {
                throw new ArgumentOutOfRangeException("participantCount", participantCount,
                    "Barrier_RemoveParticipants_NonPositive_ArgumentOutOfRange");
            }

            // in case of this is called from the PHA
            if (m_actionCallerID != 0 && Thread.CurrentThread.ManagedThreadId == m_actionCallerID)
            {
                throw new InvalidOperationException("Barrier_InvalidOperation_CalledFromPHA");
            }

            SpinWait spinner = new SpinWait();
            while (true)
            {
                int currentTotal = m_currentTotalCount;
                int total;
                int current;
                bool sense;
                GetCurrentTotal(currentTotal, out current, out total, out sense);

                if (total < participantCount)
                {
                    throw new ArgumentOutOfRangeException("participantCount",
                        "Barrier_RemoveParticipants_ArgumentOutOfRange");
                }
                if (total - participantCount < current)
                {
                    throw new InvalidOperationException("Barrier_RemoveParticipants_InvalidOperation");
                }
                // If the remaining participats = current participants, then finish the current phase
                int remaingParticipants = total - participantCount;
                if (remaingParticipants > 0 && current == remaingParticipants )
                {
                    if (SetCurrentTotal(currentTotal, 0, total - participantCount, !sense))
                    {
                        FinishPhase(sense);
                        break;
                    }
                }
                else
                {
                    if (SetCurrentTotal(currentTotal, current, total - participantCount, sense))
                    {
                        break;
                    }
                }
                spinner.SpinOnce();
            }
        }

        /// <summary>
        /// Signals that a participant has reached the <see cref="Barrier"/> and waits for all other
        /// participants to reach the barrier as well.
        /// </summary>
        /// <exception cref="T:System.InvalidOperationException">
        /// The method was invoked from within a post-phase action, the barrier currently has 0 participants,
        /// or the barrier is being used by more threads than are registered as participants.
        /// </exception>
        /// <exception cref="T:System.ObjectDisposedException">The current instance has already been
        /// disposed.</exception>
        public void SignalAndWait()
        {
            SignalAndWait(new CancellationToken());
        }

        /// <summary>
        /// Signals that a participant has reached the <see cref="Barrier"/> and waits for all other
        /// participants to reach the barrier, while observing a <see
        /// cref="T:System.Threading.CancellationToken"/>.
        /// </summary>
        /// <param name="cancellationToken">The <see cref="T:System.Threading.CancellationToken"/> to
        /// observe.</param>
        /// <exception cref="T:System.InvalidOperationException">
        /// The method was invoked from within a post-phase action, the barrier currently has 0 participants,
        /// or the barrier is being used by more threads than are registered as participants.
        /// </exception>
        /// <exception cref="T:System.OperationCanceledException"><paramref name="cancellationToken"/> has been
        /// canceled.</exception>
        /// <exception cref="T:System.ObjectDisposedException">The current instance has already been
        /// disposed.</exception>
        public void SignalAndWait(CancellationToken cancellationToken)
        {
#if DEBUG
            bool result =
#endif
            SignalAndWait(Timeout.Infinite, cancellationToken);
#if DEBUG
            Debug.Assert(result);
#endif  
        }

        /// <summary>
        /// Signals that a participant has reached the <see cref="Barrier"/> and waits for all other
        /// participants to reach the barrier as well, using a
        /// <see cref="T:System.TimeSpan"/> to measure the time interval.
        /// </summary>
        /// <param name="timeout">A <see cref="T:System.TimeSpan"/> that represents the number of
        /// milliseconds to wait, or a <see cref="T:System.TimeSpan"/> that represents -1 milliseconds to
        /// wait indefinitely.</param>
        /// <returns>true if all other participants reached the barrier; otherwise, false.</returns>
        /// <exception cref="T:System.ArgumentOutOfRangeException"><paramref name="timeout"/>is a negative number
        /// other than -1 milliseconds, which represents an infinite time-out, or it is greater than
        /// <see cref="T:System.Int32.MaxValue"/>.</exception>
        /// <exception cref="T:System.InvalidOperationException">
        /// The method was invoked from within a post-phase action, the barrier currently has 0 participants,
        /// or the barrier is being used by more threads than are registered as participants.
        /// </exception>
        /// <exception cref="T:System.ObjectDisposedException">The current instance has already been
        /// disposed.</exception>
        public Boolean SignalAndWait(TimeSpan timeout)
        {
            return SignalAndWait(timeout, new CancellationToken());
        }

        /// <summary>
        /// Signals that a participant has reached the <see cref="Barrier"/> and waits for all other
        /// participants to reach the barrier as well, using a
        /// <see cref="T:System.TimeSpan"/> to measure the time interval, while observing a <see
        /// cref="T:System.Threading.CancellationToken"/>.
        /// </summary>
        /// <param name="timeout">A <see cref="T:System.TimeSpan"/> that represents the number of
        /// milliseconds to wait, or a <see cref="T:System.TimeSpan"/> that represents -1 milliseconds to
        /// wait indefinitely.</param>
        /// <param name="cancellationToken">The <see cref="T:System.Threading.CancellationToken"/> to
        /// observe.</param>
        /// <returns>true if all other participants reached the barrier; otherwise, false.</returns>
        /// <exception cref="T:System.ArgumentOutOfRangeException"><paramref name="timeout"/>is a negative number
        /// other than -1 milliseconds, which represents an infinite time-out.</exception>
        /// <exception cref="T:System.InvalidOperationException">
        /// The method was invoked from within a post-phase action, the barrier currently has 0 participants,
        /// or the barrier is being used by more threads than are registered as participants.
        /// </exception>
        /// <exception cref="T:System.OperationCanceledException"><paramref name="cancellationToken"/> has been
        /// canceled.</exception>
        /// <exception cref="T:System.ObjectDisposedException">The current instance has already been
        /// disposed.</exception>
        public Boolean SignalAndWait(TimeSpan timeout, CancellationToken cancellationToken)
        {
            Int64 totalMilliseconds = (Int64)timeout.TotalMilliseconds;
            if (totalMilliseconds < -1 || totalMilliseconds > int.MaxValue)
            {
                throw new System.ArgumentOutOfRangeException("timeout", timeout,
                    "Barrier_SignalAndWait_ArgumentOutOfRange");
            }
            return SignalAndWait((int)timeout.TotalMilliseconds, cancellationToken);
        }

        /// <summary>
        /// Signals that a participant has reached the <see cref="Barrier"/> and waits for all other
        /// participants to reach the barrier as well, using a
        /// 32-bit signed integer to measure the time interval.
        /// </summary>
        /// <param name="millisecondsTimeout">The number of milliseconds to wait, or <see
        /// cref="Timeout.Infinite"/>(-1) to wait indefinitely.</param>
        /// <returns>true if all other participants reached the barrier; otherwise, false.</returns>
        /// <exception cref="T:System.ArgumentOutOfRangeException"><paramref name="millisecondsTimeout"/> is a
        /// negative number other than -1, which represents an infinite time-out.</exception>
        /// <exception cref="T:System.InvalidOperationException">
        /// The method was invoked from within a post-phase action, the barrier currently has 0 participants,
        /// or the barrier is being used by more threads than are registered as participants.
        /// </exception>
        /// <exception cref="T:System.ObjectDisposedException">The current instance has already been
        /// disposed.</exception>
        public bool SignalAndWait(int millisecondsTimeout)
        {
            return SignalAndWait(millisecondsTimeout, new CancellationToken());
        }

        /// <summary>
        /// Signals that a participant has reached the barrier and waits for all other participants to reach
        /// the barrier as well, using a
        /// 32-bit signed integer to measure the time interval, while observing a <see
        /// cref="T:System.Threading.CancellationToken"/>.
        /// </summary>
        /// <param name="millisecondsTimeout">The number of milliseconds to wait, or <see
        /// cref="Timeout.Infinite"/>(-1) to wait indefinitely.</param>
        /// <param name="cancellationToken">The <see cref="T:System.Threading.CancellationToken"/> to
        /// observe.</param>
        /// <returns>true if all other participants reached the barrier; otherwise, false.</returns>
        /// <exception cref="T:System.ArgumentOutOfRangeException"><paramref name="millisecondsTimeout"/> is a
        /// negative number other than -1, which represents an infinite time-out.</exception>
        /// <exception cref="T:System.InvalidOperationException">
        /// The method was invoked from within a post-phase action, the barrier currently has 0 participants,
        /// or the barrier is being used by more threads than are registered as participants.
        /// </exception>
        /// <exception cref="T:System.OperationCanceledException"><paramref name="cancellationToken"/> has been
        /// canceled.</exception>
        /// <exception cref="T:System.ObjectDisposedException">The current instance has already been
        /// disposed.</exception>
        public bool SignalAndWait(int millisecondsTimeout, CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();

            if (millisecondsTimeout < -1)
            {
                throw new System.ArgumentOutOfRangeException("millisecondsTimeout", millisecondsTimeout,
                    "Barrier_SignalAndWait_ArgumentOutOfRange");
            }

            // in case of this is called from the PHA
            if (m_actionCallerID != 0 && Thread.CurrentThread.ManagedThreadId == m_actionCallerID)
            {
                throw new InvalidOperationException("Barrier_InvalidOperation_CalledFromPHA");
            }

            // local variables to extract the basic barrier variable and update them
            // The are declared here instead of inside the loop body because the will be used outside the loop
            bool sense;
            int total;
            int current;
            int currentTotal;
            SpinWait spinner = new SpinWait();
            while (true)
            {
                currentTotal = m_currentTotalCount;
                GetCurrentTotal(currentTotal, out current, out total, out sense);
                // throw if zero participants
                if (total == 0)
                {
                    throw new InvalidOperationException("Barrier_SignalAndWait_InvalidOperation_ZeroTotal");
                }
                // Try to detect if the number of threads for this phase exceeded the total number of participants or not
                // This can be detected if the current is zero which means all participants for that phase has arrived and the phase number is not changed yet
                if (current == 0 && sense != (m_currentPhase % 2 == 0))
                {
                    throw new InvalidOperationException("Barrier_SignalAndWait_InvalidOperation_ThreadsExceeded");
                }
                //This is the last thread, finish the phase
                if (current + 1 == total)
                {
                    if (SetCurrentTotal(currentTotal, 0, total, !sense))
                    {
#if !FEATURE_PAL    // PAL doesn't support  eventing
                        if (CdsSyncEtwBCLProvider.Log.IsEnabled())
                        {
                            CdsSyncEtwBCLProvider.Log.Barrier_PhaseFinished(sense, m_currentPhase);
                        }
#endif
                        FinishPhase(sense);
                        return true;
                    }
                }
                else if (SetCurrentTotal(currentTotal, current + 1, total, sense))
                {
                    break;
                }

                spinner.SpinOnce();

            }
            // save the phase locally
            long phase = m_currentPhase;
            
            // ** Perform the real wait **
            // select the correct event to wait on, based on the current sense.
            ManualResetEventSlim eventToWaitOn = (sense) ? m_evenEvent : m_oddEvent;
            bool waitWasCanceled = false;
            bool waitResult = false;
            try
            {
                waitResult = eventToWaitOn.Wait(millisecondsTimeout, cancellationToken);
            }
            catch (OperationCanceledException )
            {
                waitWasCanceled = true;
            }


            if (!waitResult)
            {
                //reset the spinLock to prepare it for the next loop
                spinner.Reset();

                //If the wait timeout expired and all other thread didn't reach the barrier yet, update the current count back
                while (true)
                {
                    bool newSense;
                    currentTotal = m_currentTotalCount;
                    GetCurrentTotal(currentTotal, out current, out total, out newSense);
                    // If the timeout expired and the phase has just finished, return true and this is considered as succeeded SignalAndWait
                    //otherwise the timeout expired and the current phase has not been finished yet, return false
                    //The phase is finished if the phase member variable is changed (incremented) or the sense has been changed
                    if (phase != m_currentPhase || sense != newSense)
                    {
                        // The current phase has been finished, but we shouldn't return before the events are set/reset otherwise this thread could start
                        // next phase and the appropriate event has not reset yet which could make it return immediately from the next phase SignalAndWait
                        // before waiting other threads
                        eventToWaitOn.Wait();
                        Debug.Assert(phase < m_currentPhase); // assert that the phase has been completely finished
                       
                        //if here, then all the other participants had reached the barrier and moved to the
                        //next phase. Hence we cannot (and should not) backout the signal and can return true.
                        break;
                    }
                    //The phase has not been finished yet, try to update the current count.
                    if (SetCurrentTotal(currentTotal, current - 1, total, sense))
                    {
                        //if here, then the attempt to backout was successful.
                        //throw (a fresh) oce if cancellation woke the wait
                        //or return false if it was the timeout that woke the wait.
                        //
                        if (waitWasCanceled)
                        {
#if PFX_LEGACY_3_5
                            throw new OperationCanceledException2("Common_OperationCanceled", cancellationToken);
#else
                            throw new OperationCanceledException("Common_OperationCanceled", cancellationToken);
#endif
                        }
                        else
                            return false;
                    }
                    spinner.SpinOnce();
                }
            }

            if (m_exception != null)
                throw new BarrierPostPhaseException(m_exception);

            return true;

        }

        /// <summary>
        /// Finish the phase by invoking the post phase action, and setting the event, this must be called by the 
        /// last arrival thread
        /// </summary>
        /// <param name="observedSense">The current phase sense</param>
        private void FinishPhase(bool observedSense)
        {
            // Execute the PHA in try/finally block to reset the variables back in case of it threw an exception
            if (m_postPhaseAction != null)
            {
                try
                {
                    // Capture the caller thread ID to check if the Add/RemoveParticipant(s) is called from the PHA
                    m_actionCallerID = Thread.CurrentThread.ManagedThreadId;
                    if (m_ownerThreadContext != null)
                    {
                        ExecutionContext.Run(m_ownerThreadContext.CreateCopy(), i => m_postPhaseAction(this), null);
                    }
                    else
                    {
                        m_postPhaseAction(this);
                    }
                    m_exception = null; // reset the exception if it was set previously
                }
                catch (Exception ex)
                {
                    m_exception = ex;
                }
                finally
                {
                    m_actionCallerID = 0;
                    SetResetEvents(observedSense);
                    if(m_exception != null)
                        throw new BarrierPostPhaseException(m_exception);
                }

            }
            else
            {
                SetResetEvents(observedSense);
            }
        }

        /// <summary>
        /// Sets the current phase event and reset the next phase event
        /// </summary>
        /// <param name="observedSense">The current phase sense</param>
        private void SetResetEvents(bool observedSense)
        {
            // Increment the phase count
            m_currentPhase++;
            if (observedSense)
            {
                m_oddEvent.Reset();
                m_evenEvent.Set();
            }
            else
            {
                m_evenEvent.Reset();
                m_oddEvent.Set();
            }
        }

        /// <summary>
        /// Releases all resources used by the current instance of <see cref="Barrier"/>.
        /// </summary>
        /// <exception cref="T:System.InvalidOperationException">
        /// The method was invoked from within a post-phase action.
        /// </exception>
        /// <remarks>
        /// Unlike most of the members of <see cref="Barrier"/>, Dispose is not thread-safe and may not be
        /// used concurrently with other members of this instance.
        /// </remarks>
        public void Dispose()
        {
            // in case of this is called from the PHA
            if (m_actionCallerID != 0 && Thread.CurrentThread.ManagedThreadId == m_actionCallerID)
            {
                throw new InvalidOperationException("Barrier_InvalidOperation_CalledFromPHA");
            }
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// When overridden in a derived class, releases the unmanaged resources used by the
        /// <see cref="Barrier"/>, and optionally releases the managed resources.
        /// </summary>
        /// <param name="disposing">true to release both managed and unmanaged resources; false to release
        /// only unmanaged resources.</param>
        /// <remarks>
        /// Unlike most of the members of <see cref="Barrier"/>, Dispose is not thread-safe and may not be
        /// used concurrently with other members of this instance.
        /// </remarks>
        protected virtual void Dispose(bool disposing)
        {
            if (!m_disposed)
            {
                if (disposing)
                {
                    m_oddEvent.Dispose();
                    m_evenEvent.Dispose();
                }
                m_disposed = true;
            }
        }

        /// <summary>
        /// Throw ObjectDisposedException if the barrier is disposed
        /// </summary>
        private void ThrowIfDisposed()
        {
            if (m_disposed)
            {
                throw new ObjectDisposedException("Barrier", "Barrier_Dispose");
            }
        }
    }
}
